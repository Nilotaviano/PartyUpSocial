import { useCallback, useEffect, useRef, useState } from "react";
import {
  DEFAULT_STEP_MIN,
  DEFAULT_TOUCH_CREATE_MIN,
  clampMinutes,
  snapMinutes,
  type GridBlock,
  type GridDrag,
  type GridEdge,
  type GridPoint,
} from "./gridEditing";

/**
 * The pointer/touch interaction core shared by every week-shaped editing grid
 * (the calendar's `WeekGrid`, and the poll grids built on the same model).
 *
 * It owns the three iOS-hard-won behaviours together, because none of them
 * works alone: a long press arms painting on touch (a plain finger drag must
 * still scroll the page), a non-passive `touchmove` veto refuses the pan the
 * browser has already granted, and a window auto-scroll keeps rows below the
 * fold reachable mid-drag.
 *
 * It knows nothing about what a block *is*. Consumers hand it a config of
 * gesture switches and callbacks, spread the returned prop bundles onto their
 * own markup, and render previews from the returned `drag`.
 */

/**
 * A drag whose pointer sits within this many pixels of a viewport edge keeps
 * the page moving under it — grids render at natural height and the document
 * scrolls, so a touch drag could otherwise never reach a row below the fold.
 */
const AUTO_SCROLL_EDGE_PX = 48;
const AUTO_SCROLL_STEP_PX = 16;
const AUTO_SCROLL_TICK_MS = 60;

const LONG_PRESS_MS = 380;

/**
 * A finger never holds perfectly still: only movement past this radius is a
 * real scroll reclaiming the gesture from a pending long press. Cancelling on
 * any movement at all would make the press impossible to hold on a touchscreen.
 */
const MOVE_CANCEL_PX = 10;

export type GridGestureConfig = {
  /**
   * The element containing the `[data-daycol]` columns. The consumer owns it
   * so that nothing the hook returns is ref-shaped: a returned ref taints the
   * whole result object for React's compiler, which then refuses every read of
   * it during render.
   */
  columnsRef: React.RefObject<HTMLDivElement | null>;
  /**
   * Host for the `touchmove` veto, when the pointer target is not a descendant
   * of `columnsRef` (a cell layer that is the columns' sibling). Defaults to
   * the columns element.
   */
  hostRef?: React.RefObject<HTMLElement | null>;
  rangeStart: number;
  rangeEnd: number;
  /** Snap lattice for paint-create drags. */
  createStepMin?: number;
  /** Snap lattice for whole-block move drags. Defaults to `createStepMin`. */
  moveStepMin?: number;
  /** Snap lattice for edge resizes, which may be finer than the create step. */
  resizeStepMin?: number;
  /** Shortest range that commits, and the floor a resize may not cross. */
  minDurationMin?: number;
  /** Length a mouse paint starts at, before the pointer moves. */
  initialCreateMin?: number;
  /** Length a long-press paint starts at. */
  initialTouchCreateMin?: number;
  longPressMs?: number;
  createEnabled?: boolean;
  moveEnabled?: boolean;
  resizeEnabled?: boolean;
  /** A grid at its block cap refuses to start a create gesture at all. */
  capReached?: boolean;
  /**
   * A create gesture began: mouse presses report immediately, touch presses
   * only once the long press has armed. Use it to clear a selection or to
   * announce the start to a live region.
   */
  onCreateStart?: (day: number, startMin: number, pointerType: string) => void;
  /** The cap refused a create press, so the consumer can say why. */
  onCreateBlocked?: (day: number) => void;
  onBlockCreate?: (day: number, startMin: number, endMin: number, at: GridPoint) => void;
  onBlockMove?: (blockId: string, day: number, startMin: number, endMin: number) => void;
  onBlockResize?: (blockId: string, startMin: number, endMin: number) => void;
  /** A press on a block, before any drag begins. */
  onBlockSelect?: (blockId: string) => void;
  /**
   * A press on a block that never became a drag — the tap-to-vote/tap-to-remove
   * path. Only ever fires when `moveEnabled` is on, since a grid without move
   * gestures cannot tell a tap from a plain selection press.
   */
  onBandTap?: (blockId: string) => void;
};

/** Handlers a day column (or the cell layer above one) spreads to paint on. */
export type GridColumnProps = {
  "data-daycol": number;
  onPointerDown: (e: React.PointerEvent<HTMLElement>) => void;
  onPointerUp: () => void;
  onPointerCancel: () => void;
  onPointerMove: (e: React.PointerEvent<HTMLElement>) => void;
  onContextMenu: (e: React.MouseEvent) => void;
};

/** Handlers a block's own body spreads to be selected, moved or tapped. */
export type GridBlockPressProps = {
  onPointerDown: (e: React.PointerEvent<HTMLElement>) => void;
  onPointerMove: (e: React.PointerEvent<HTMLElement>) => void;
  onPointerUp?: () => void;
  onPointerCancel: () => void;
};

export type GridGestures = {
  drag: GridDrag | null;
  /** A long press has armed on touch: the grid, not the page, owns the finger. */
  armed: boolean;
  /** Armed or dragging — the state that must lock `touch-action`. */
  active: boolean;
  columnProps: (day: number) => GridColumnProps;
  blockPressProps: (block: GridBlock) => GridBlockPressProps;
  resizeHandleProps: (
    block: GridBlock,
    edge: GridEdge,
  ) => { onPointerDown: (e: React.PointerEvent<HTMLElement>) => void };
  /** The live drag on this day, whatever its mode, or null. */
  previewFor: (day: number) => GridDrag | null;
  abortGesture: () => void;
  cancelPress: () => void;
  minuteAt: (clientY: number, el: HTMLElement, step: number) => number;
};

export function useGridGestures(config: GridGestureConfig): GridGestures {
  const { rangeStart, rangeEnd } = config;
  const [drag, setDrag] = useState<GridDrag | null>(null);
  const [armed, setArmed] = useState(false);
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const touchOrigin = useRef<{ x: number; y: number } | null>(null);
  /**
   * The committed drag is read from here, never from `drag`: a pointermove's
   * render can still be pending when the pointerup lands, so every mutation
   * writes this ref in the same DOM event that queues the state update.
   */
  const dragRef = useRef<GridDrag | null>(null);
  /**
   * Mirrors `armed` for the DOM-level `touchmove` veto below, which runs
   * outside React's render and cannot read state.
   */
  const armedRef = useRef(false);
  /** Callbacks and switches are read at gesture time, never captured stale. */
  const configRef = useRef(config);
  useEffect(() => {
    configRef.current = config;
  });

  const applyDrag = useCallback((next: GridDrag | null) => {
    dragRef.current = next;
    setDrag(next);
  }, []);

  const totalMin = rangeEnd - rangeStart;
  const minuteAt = useCallback(
    (clientY: number, el: HTMLElement, step: number) => {
      const rect = el.getBoundingClientRect();
      const raw = rangeStart + ((clientY - rect.top) / (rect.height || 1)) * totalMin;
      return clampMinutes(snapMinutes(raw, step), rangeStart, rangeEnd);
    },
    [rangeStart, rangeEnd, totalMin],
  );

  const columnOfDay = useCallback(
    (day: number) =>
      configRef.current.columnsRef.current?.querySelector<HTMLElement>(
        `[data-daycol="${day}"]`,
      ) ?? null,
    [],
  );

  const cancelPress = useCallback(() => {
    if (pressTimer.current) clearTimeout(pressTimer.current);
    pressTimer.current = null;
    touchOrigin.current = null;
  }, []);

  /**
   * A gesture the browser takes back (`pointercancel`, fired whenever iOS
   * decides the touch was a pan) must leave nothing behind: no pending press,
   * no armed state and no half-built drag that a later pointerup would commit.
   */
  const abortGesture = useCallback(() => {
    cancelPress();
    armedRef.current = false;
    setArmed(false);
    applyDrag(null);
  }, [applyDrag, cancelPress]);

  /**
   * iOS locks `touch-action` when a gesture starts, so flipping a class
   * mid-gesture cannot stop the pan it already granted. Vetoing `touchmove`
   * from a non-passive listener is the only scroll refusal it honors; it
   * applies while a press is pending or armed, and stops the moment the press
   * is cancelled so a scroll keeps scrolling.
   */
  useEffect(() => {
    const el = configRef.current.hostRef?.current ?? configRef.current.columnsRef.current;
    if (!el) return;
    const onTouchMove = (event: TouchEvent) => {
      if (pressTimer.current === null && !armedRef.current) return;
      event.preventDefault();
    };
    el.addEventListener("touchmove", onTouchMove, { passive: false });
    return () => el.removeEventListener("touchmove", onTouchMove);
  }, []);

  useEffect(() => cancelPress, [cancelPress]);

  // Global pointer handling while dragging. The effect is keyed on whether a
  // drag is live, never on the drag itself: re-running it on every pointermove
  // would restart the auto-scroll interval and throw away the pointer position
  // it steers by, so a drag could never reach a row below the fold.
  const dragging = drag !== null;
  useEffect(() => {
    if (!dragging) return;
    let pointerY: number | null = null;
    const columnOf = () =>
      configRef.current.columnsRef.current?.querySelector<HTMLElement>(
        `[data-daycol="${dragRef.current ? dragRef.current.day : ""}"]`,
      );
    const minDuration = () => configRef.current.minDurationMin ?? DEFAULT_STEP_MIN;
    const stepOf = (d: GridDrag) =>
      d.mode === "resize"
        ? (configRef.current.resizeStepMin ?? DEFAULT_STEP_MIN)
        : d.mode === "move"
          ? (configRef.current.moveStepMin ??
            configRef.current.createStepMin ??
            DEFAULT_STEP_MIN)
          : (configRef.current.createStepMin ?? DEFAULT_STEP_MIN);
    const extendTo = (m: number) => {
      const d = dragRef.current;
      if (!d) return;
      if (d.mode === "create") {
        const a = Math.min(d.anchor, m);
        const b = Math.max(d.anchor, m);
        applyDrag({ ...d, a, b, painted: d.painted || b - a >= minDuration() });
      } else if (d.mode === "move") {
        // The block's start follows the pointer and its length rides along.
        applyDrag({ ...d, a: m, b: m + (d.originEnd - d.originStart) });
      } else if (d.edge === "start") {
        applyDrag({ ...d, a: Math.min(m, d.b - minDuration()) });
      } else {
        applyDrag({ ...d, b: Math.max(m, d.a + minDuration()) });
      }
    };
    const move = (e: PointerEvent) => {
      const col = columnOf();
      const d = dragRef.current;
      if (!col || !d) return;
      pointerY = e.clientY;
      extendTo(minuteAt(e.clientY, col, stepOf(d)));
      e.preventDefault();
    };
    const autoScroll = setInterval(() => {
      if (pointerY === null) return;
      const distanceToBottom = window.innerHeight - pointerY;
      const step =
        pointerY < AUTO_SCROLL_EDGE_PX
          ? -AUTO_SCROLL_STEP_PX
          : distanceToBottom < AUTO_SCROLL_EDGE_PX
            ? AUTO_SCROLL_STEP_PX
            : 0;
      if (step === 0) return;
      window.scrollBy(0, step);
      const col = columnOf();
      const d = dragRef.current;
      if (col && d) extendTo(minuteAt(pointerY, col, stepOf(d)));
    }, AUTO_SCROLL_TICK_MS);
    const up = (e: PointerEvent) => {
      const d = dragRef.current;
      applyDrag(null);
      setArmed(false);
      armedRef.current = false;
      if (!d) return;
      const c = configRef.current;
      if (d.mode === "create") {
        // A draft shorter than the minimum is not a paint — the browser can
        // hand one back after it steals a gesture, and a press that never grew
        // is a tap the consumer's own tap path owns.
        if (d.b - d.a < (c.minDurationMin ?? DEFAULT_STEP_MIN)) return;
        c.onBlockCreate?.(d.day, d.a, d.b, { x: e.clientX, y: e.clientY });
        return;
      }
      if (d.mode === "move") {
        if (d.a === d.originStart) {
          c.onBandTap?.(d.blockId);
          return;
        }
        c.onBlockMove?.(d.blockId, d.day, d.a, d.b);
        return;
      }
      if (d.a !== d.originStart || d.b !== d.originEnd) c.onBlockResize?.(d.blockId, d.a, d.b);
    };
    window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", abortGesture);
    return () => {
      clearInterval(autoScroll);
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", abortGesture);
    };
  }, [abortGesture, applyDrag, dragging, minuteAt]);

  /** Arms a gesture behind a long press on touch, or starts it now on mouse. */
  const press = useCallback(
    (e: React.PointerEvent<HTMLElement>, begin: () => void) => {
      if (e.pointerType !== "touch") {
        begin();
        return;
      }
      // A finger drag must scroll the page — editing requires a long press.
      touchOrigin.current = { x: e.clientX, y: e.clientY };
      pressTimer.current = setTimeout(
        () => {
          pressTimer.current = null;
          touchOrigin.current = null;
          armedRef.current = true;
          setArmed(true);
          if (navigator.vibrate) navigator.vibrate(12);
          begin();
        },
        configRef.current.longPressMs ?? LONG_PRESS_MS,
      );
    },
    [],
  );

  const pressMove = useCallback(
    (e: React.PointerEvent<HTMLElement>) => {
      const origin = touchOrigin.current;
      if (!origin) return;
      if (Math.hypot(e.clientX - origin.x, e.clientY - origin.y) > MOVE_CANCEL_PX) cancelPress();
    },
    [cancelPress],
  );

  const columnProps = useCallback(
    (day: number): GridColumnProps => ({
      "data-daycol": day,
      onPointerDown: (e) => {
        if (e.button === 2) return;
        const c = configRef.current;
        if (c.createEnabled === false) return;
        if (c.capReached) {
          c.onCreateBlocked?.(day);
          return;
        }
        const column = columnOfDay(day) ?? e.currentTarget;
        const clientY = e.clientY;
        const pointerType = e.pointerType;
        press(e, () => {
          const step = configRef.current.createStepMin ?? DEFAULT_STEP_MIN;
          const m = minuteAt(clientY, column, step);
          const initial =
            pointerType === "touch"
              ? (configRef.current.initialTouchCreateMin ?? DEFAULT_TOUCH_CREATE_MIN)
              : (configRef.current.initialCreateMin ?? 0);
          configRef.current.onCreateStart?.(day, m, pointerType);
          applyDrag({
            mode: "create",
            day,
            anchor: m,
            a: m,
            b: m + initial,
            painted: initial >= (configRef.current.minDurationMin ?? DEFAULT_STEP_MIN),
          });
        });
      },
      onPointerUp: cancelPress,
      onPointerCancel: abortGesture,
      onPointerMove: pressMove,
      onContextMenu: (e) => e.preventDefault(),
    }),
    [abortGesture, applyDrag, cancelPress, columnOfDay, minuteAt, press, pressMove],
  );

  const blockPressProps = useCallback(
    (block: GridBlock): GridBlockPressProps => {
      const moveEnabled = config.moveEnabled === true;
      return {
        onPointerDown: (e) => {
          e.stopPropagation();
          configRef.current.onBlockSelect?.(block.id);
          if (e.button === 2 || configRef.current.moveEnabled !== true) return;
          press(e, () =>
            applyDrag({
              mode: "move",
              day: block.day,
              blockId: block.id,
              a: block.startMin,
              b: block.endMin,
              originStart: block.startMin,
              originEnd: block.endMin,
            }),
          );
        },
        onPointerMove: pressMove,
        // A touch press that ends before the long press armed never became a
        // move: it is the tap path, and only a move-capable grid has one.
        onPointerUp: moveEnabled
          ? () => {
              if (!pressTimer.current) return;
              cancelPress();
              configRef.current.onBandTap?.(block.id);
            }
          : undefined,
        onPointerCancel: abortGesture,
      };
    },
    [abortGesture, applyDrag, cancelPress, config.moveEnabled, press, pressMove],
  );

  const resizeHandleProps = useCallback(
    (block: GridBlock, edge: GridEdge) => ({
      onPointerDown: (e: React.PointerEvent<HTMLElement>) => {
        e.stopPropagation();
        if (configRef.current.resizeEnabled === false) return;
        applyDrag({
          mode: "resize",
          day: block.day,
          blockId: block.id,
          edge,
          a: block.startMin,
          b: block.endMin,
          originStart: block.startMin,
          originEnd: block.endMin,
        });
      },
    }),
    [applyDrag],
  );

  const previewFor = useCallback(
    (day: number) => (drag && drag.day === day ? drag : null),
    [drag],
  );

  return {
    drag,
    armed,
    active: armed || drag !== null,
    columnProps,
    blockPressProps,
    resizeHandleProps,
    previewFor,
    abortGesture,
    cancelPress,
    minuteAt,
  };
}
