import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { GripHorizontal } from "lucide-react";
import { cn } from "./utils";
import {
  ROW_H,
  SLOT_MIN,
  bandsForWeek,
  clamp,
  fmt,
  fmtRange,
  snap,
  type AvailabilityRule,
  type Band,
  type Friend,
  type GameSession,
  type MergedSpan,
  type PollOption,
  type WeekLabels,
} from "./partyup-data";

/**
 * A drag whose pointer sits within this many pixels of a viewport edge keeps
 * the page moving under it — the grid renders at natural height and the
 * document scrolls, so a touch drag could otherwise never reach a row below
 * the fold.
 */
const AUTO_SCROLL_EDGE_PX = 48;
const AUTO_SCROLL_STEP_PX = 16;
const AUTO_SCROLL_TICK_MS = 60;

export type Draft = {
  day: number;
  start: number;
  end: number;
  x: number;
  y: number;
};

type Props = {
  rules: AvailabilityRule[];
  /**
   * The server's own merged ranges (A-076): the busy state the cell layer
   * reports is read from these verbatim, never re-derived from `rules` — the
   * two can legitimately differ and the server's answer is the authority.
   */
  merged: MergedSpan[];
  friends: Friend[];
  sessions: GameSession[];
  polls: PollOption[];
  labels: WeekLabels;
  days: number[];
  rangeStart: number;
  rangeEnd: number;
  todayIndex: number;
  nowMinutes: number | null;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onDraftComplete: (d: Draft) => void;
  onResize: (ruleId: string, start: number, end: number) => void;
  /** Keyboard navigation past the shown day window pages it (CALENDAR §9 S5). */
  onPageStep?: (delta: -1 | 1) => void;
};

type Drag =
  | { mode: "new"; day: number; anchor: number; a: number; b: number }
  | {
      mode: "resize";
      day: number;
      ruleId: string;
      edge: "start" | "end";
      a: number;
      b: number;
      /** Where the band sat when the handle was grabbed — a drag that ends here wrote nothing. */
      originStart: number;
      originEnd: number;
    }
  | null;

export function WeekGrid({
  rules,
  merged,
  friends,
  sessions: allSessions,
  polls: allPolls,
  labels,
  days,
  rangeStart,
  rangeEnd,
  todayIndex,
  nowMinutes,
  selectedId,
  onSelect,
  onDraftComplete,
  onResize,
  onPageStep,
}: Props) {
  const { t } = useTranslation("availability");
  const [drag, setDrag] = useState<Drag>(null);
  const [armed, setArmed] = useState(false); // long-press armed on touch
  const columnsRef = useRef<HTMLDivElement>(null);
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const dragRef = useRef<Drag>(null);

  const totalMin = rangeEnd - rangeStart;
  const height = (totalMin / SLOT_MIN) * ROW_H;
  const rowCount = Math.round(totalMin / SLOT_MIN);
  const bands = bandsForWeek(rules);

  // Roving tab stop over the accessible cell layer (ARIA grid pattern), plus
  // the keyboard selection anchor the Enter/Space path builds a draft from.
  const [focusCell, setFocusCell] = useState({ col: 0, row: 0 });
  const [keyAnchor, setKeyAnchor] = useState<{ col: number; row: number } | null>(null);
  const [liveMessage, setLiveMessage] = useState("");
  const focusCol = clamp(focusCell.col, 0, Math.max(days.length - 1, 0));
  const focusRow = clamp(focusCell.row, 0, Math.max(rowCount - 1, 0));

  const yToMin = useCallback(
    (clientY: number, el: HTMLElement) => {
      const rect = el.getBoundingClientRect();
      const raw = rangeStart + ((clientY - rect.top) / rect.height) * totalMin;
      return clamp(snap(raw), rangeStart, rangeEnd);
    },
    [rangeStart, rangeEnd, totalMin],
  );

  const top = (m: number) => ((m - rangeStart) / SLOT_MIN) * ROW_H;
  const hgt = (a: number, b: number) => ((b - a) / SLOT_MIN) * ROW_H;

  const onDraftCompleteRef = useRef(onDraftComplete);
  const onResizeRef = useRef(onResize);
  useEffect(() => {
    dragRef.current = drag;
    onDraftCompleteRef.current = onDraftComplete;
    onResizeRef.current = onResize;
  });

  // Global pointer handling while dragging. The effect is keyed on whether a
  // drag is live, never on the drag itself: re-running it on every pointermove
  // would restart the auto-scroll interval and throw away the pointer position
  // it steers by, so a drag could never reach a row below the fold.
  const dragging = drag !== null;
  useEffect(() => {
    if (!dragging) return;
    let pointerY: number | null = null;
    const columnOf = () =>
      columnsRef.current?.querySelector<HTMLElement>(
        `[data-daycol="${dragRef.current && "day" in dragRef.current ? dragRef.current.day : ""}"]`,
      );
    const extendTo = (m: number) => {
      setDrag((d) => {
        if (!d) return d;
        if (d.mode === "new") {
          return { ...d, a: Math.min(d.anchor, m), b: Math.max(d.anchor, m) };
        }
        if (d.edge === "start") return { ...d, a: Math.min(m, d.b - SLOT_MIN) };
        return { ...d, b: Math.max(m, d.a + SLOT_MIN) };
      });
    };
    const move = (e: PointerEvent) => {
      const col = columnOf();
      if (!col) return;
      pointerY = e.clientY;
      extendTo(yToMin(e.clientY, col));
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
      if (col) extendTo(yToMin(pointerY, col));
    }, AUTO_SCROLL_TICK_MS);
    const up = (e: PointerEvent) => {
      const d = dragRef.current;
      setDrag(null);
      setArmed(false);
      if (!d) return;
      if (d.mode === "new") {
        if (d.b - d.a < SLOT_MIN) return;
        onDraftCompleteRef.current({ day: d.day, start: d.a, end: d.b, x: e.clientX, y: e.clientY });
      } else if (d.a !== d.originStart || d.b !== d.originEnd) {
        onResizeRef.current(d.ruleId, d.a, d.b);
      }
    };
    window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
    return () => {
      clearInterval(autoScroll);
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", up);
    };
  }, [dragging, yToMin]);

  const startPaint = (e: React.PointerEvent<HTMLDivElement>, day: number) => {
    const el = e.currentTarget;
    const m = yToMin(e.clientY, el);
    setDrag({ mode: "new", day, anchor: m, a: m, b: m + SLOT_MIN });
  };

  const onColumnPointerDown = (e: React.PointerEvent<HTMLDivElement>, day: number) => {
    if (e.button === 2) return;
    const el = e.currentTarget;
    const clientY = e.clientY;
    if (e.pointerType === "touch") {
      // finger drag must scroll the page — painting requires a long press
      pressTimer.current = setTimeout(() => {
        setArmed(true);
        if (navigator.vibrate) navigator.vibrate(12);
        const m = clamp(
          snap(rangeStart + ((clientY - el.getBoundingClientRect().top) / el.getBoundingClientRect().height) * totalMin),
          rangeStart,
          rangeEnd,
        );
        setLiveMessage(t("a11y.selectionStarted", { time: fmt(m) }));
        setDrag({ mode: "new", day, anchor: m, a: m, b: m + 30 });
      }, 380);
      return;
    }
    onSelect(null);
    startPaint(e, day);
  };

  const cancelPress = () => {
    if (pressTimer.current) clearTimeout(pressTimer.current);
    pressTimer.current = null;
  };

  const hours: number[] = [];
  for (let m = rangeStart; m <= rangeEnd; m += 60) hours.push(m);

  const minutesOfRow = (row: number) => rangeStart + row * SLOT_MIN;

  const busyAt = (day: number, row: number) => {
    const m = minutesOfRow(row);
    return merged.some((span) => span.day === day && span.start <= m && span.end > m);
  };

  function moveFocus(col: number, row: number) {
    if (col < 0) {
      onPageStep?.(-1);
      return;
    }
    if (col > days.length - 1) {
      onPageStep?.(1);
      return;
    }
    const next = { col, row: clamp(row, 0, rowCount - 1) };
    setFocusCell(next);
    const cell = columnsRef.current
      ?.closest("[data-grid-root]")
      ?.querySelector<HTMLElement>(`[data-cell="${next.col}:${next.row}"]`);
    cell?.focus();
  }

  function onCellKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    const extend = event.shiftKey && keyAnchor !== null;
    switch (event.key) {
      case "ArrowUp":
        event.preventDefault();
        moveFocus(focusCol, focusRow - 1);
        if (extend) announceExtent(clamp(focusRow - 1, 0, rowCount - 1));
        return;
      case "ArrowDown":
        event.preventDefault();
        moveFocus(focusCol, focusRow + 1);
        if (extend) announceExtent(clamp(focusRow + 1, 0, rowCount - 1));
        return;
      case "ArrowLeft":
        event.preventDefault();
        moveFocus(focusCol - 1, focusRow);
        return;
      case "ArrowRight":
        event.preventDefault();
        moveFocus(focusCol + 1, focusRow);
        return;
      case "Home":
        event.preventDefault();
        moveFocus(focusCol, 0);
        return;
      case "End":
        event.preventDefault();
        moveFocus(focusCol, rowCount - 1);
        return;
      case "Escape":
        if (keyAnchor) {
          event.preventDefault();
          setKeyAnchor(null);
          setLiveMessage(t("a11y.selectionCanceled"));
        }
        return;
      case "Enter":
      case " ":
        event.preventDefault();
        commitKeyboardStep();
        return;
      default:
    }
  }

  function announceExtent(row: number) {
    if (!keyAnchor) return;
    const a = Math.min(keyAnchor.row, row);
    const b = Math.max(keyAnchor.row, row) + 1;
    setLiveMessage(
      t("a11y.selectionExtent", { start: fmt(minutesOfRow(a)), end: fmt(minutesOfRow(b)) }),
    );
  }

  function commitKeyboardStep() {
    const day = days[focusCol];
    if (day === undefined) return;
    if (!keyAnchor || keyAnchor.col !== focusCol) {
      setKeyAnchor({ col: focusCol, row: focusRow });
      setLiveMessage(t("a11y.selectionStarted", { time: fmt(minutesOfRow(focusRow)) }));
      return;
    }
    const a = minutesOfRow(Math.min(keyAnchor.row, focusRow));
    const b = minutesOfRow(Math.max(keyAnchor.row, focusRow) + 1);
    setKeyAnchor(null);
    setLiveMessage(t("a11y.selectionReady", { start: fmt(a), end: fmt(b) }));
    const cell = columnsRef.current
      ?.closest("[data-grid-root]")
      ?.querySelector<HTMLElement>(`[data-cell="${focusCol}:${focusRow}"]`);
    const rect = cell?.getBoundingClientRect();
    onDraftComplete({
      day,
      start: a,
      end: b,
      x: rect ? rect.left + rect.width / 2 : 0,
      y: rect ? rect.top : 0,
    });
  }

  const keyPreview =
    keyAnchor && days[keyAnchor.col] !== undefined && keyAnchor.col === focusCol
      ? {
          day: days[focusCol]!,
          a: minutesOfRow(Math.min(keyAnchor.row, focusRow)),
          b: minutesOfRow(Math.max(keyAnchor.row, focusRow) + 1),
        }
      : null;

  const gridTemplate = `56px repeat(${days.length}, minmax(0,1fr))`;

  return (
    <div className="relative" data-grid-root data-touch-dragging={armed || drag !== null}>
      {/* header row */}
      <div
        className="sticky top-0 z-30 -mx-1 bg-background/92 px-1 pt-2 pb-2 backdrop-blur-md"
        style={{ gridTemplateColumns: gridTemplate }}
      >
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }}>
          <div />
          {days.map((d) => {
            const isToday = d === todayIndex;
            return (
              <div
                key={d}
                className={cn(
                  "flex min-w-0 flex-col items-center rounded-xl border px-1 py-1.5 text-center",
                  isToday
                    ? "border-primary/60 bg-primary/15"
                    : "border-border/70 bg-card/80",
                )}
              >
                <span className="font-display text-[13px] leading-tight font-bold">
                  {labels.dayNames[d]}
                </span>
                <span className="truncate text-[11px] text-muted-foreground">
                  {labels.dayDates[d]}
                </span>
              </div>
            );
          })}
        </div>
      </div>

      {/* grid body */}
      <div className="relative mt-1">
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }} ref={columnsRef}>
          {/* hour ruler */}
          <div className="relative" style={{ height }}>
            {hours.map((m) => (
              <div
                key={m}
                className="absolute right-2 -translate-y-1/2 font-display text-[11px] font-semibold text-muted-foreground tabular-nums"
                style={{ top: top(m) }}
              >
                {fmt(m)}
              </div>
            ))}
          </div>

          {days.map((day) => {
            const dayBands = bands.filter((b) => b.day === day);
            const sessions = allSessions.filter((s) => s.day === day);
            const polls = allPolls.filter((p) => p.day === day);
            const friendWindows = friends
              .flatMap((f) => f.windows.filter((w) => w.day === day).map((w) => ({ ...w, friend: f })))
              .sort((a, b) => a.start - b.start);

            // group friend chips into 30-min buckets at window starts
            const buckets = new Map<number, typeof friendWindows>();
            for (const w of friendWindows) {
              const key = Math.floor(w.start / 30) * 30;
              const arr = buckets.get(key) ?? [];
              arr.push(w);
              buckets.set(key, arr);
            }

            const isToday = day === todayIndex;
            const activeDrag =
              drag && "day" in drag && drag.day === day
                ? { a: drag.a, b: drag.b, mode: drag.mode }
                : keyPreview && keyPreview.day === day
                  ? { a: keyPreview.a, b: keyPreview.b, mode: "new" as const }
                  : null;

            return (
              <div
                key={day}
                data-daycol={day}
                onPointerDown={(e) => onColumnPointerDown(e, day)}
                onPointerUp={cancelPress}
                onPointerMove={cancelPress}
                onContextMenu={(e) => e.preventDefault()}
                className={cn(
                  "relative overflow-hidden rounded-2xl border bg-card select-none",
                  isToday ? "border-primary/40" : "border-border/70",
                  armed || drag ? "touch-none" : "touch-pan-y",
                )}
                style={{
                  height,
                  backgroundImage: `repeating-linear-gradient(to bottom, var(--grid-hour) 0 1px, transparent 1px ${ROW_H * 4}px), repeating-linear-gradient(to bottom, var(--grid-line) 0 1px, transparent 1px ${ROW_H}px)`,
                }}
              >
                {/* my availability bands */}
                {dayBands.map((b) => (
                  <BandBlock
                    key={`${b.ruleId}-${b.day}`}
                    band={b}
                    top={top(b.start)}
                    height={hgt(b.start, b.end)}
                    selected={selectedId === b.ruleId}
                    onSelect={() => onSelect(b.ruleId)}
                    onHandleDown={(edge, e) => {
                      e.stopPropagation();
                      setDrag({
                        mode: "resize",
                        day,
                        ruleId: b.ruleId,
                        edge,
                        a: b.start,
                        b: b.end,
                        originStart: b.start,
                        originEnd: b.end,
                      });
                    }}
                    onNudge={(edge, delta) => {
                      const start = edge === "start" ? Math.min(b.start + delta, b.end - SLOT_MIN) : b.start;
                      const end = edge === "end" ? Math.max(b.end + delta, b.start + SLOT_MIN) : b.end;
                      onResize(b.ruleId, clamp(start, rangeStart, rangeEnd), clamp(end, rangeStart, rangeEnd));
                    }}
                    live={
                      drag && drag.mode === "resize" && drag.ruleId === b.ruleId
                        ? { start: drag.a, end: drag.b }
                        : null
                    }
                    topOf={top}
                    heightOf={hgt}
                  />
                ))}

                {/* friends' chips */}
                {[...buckets.entries()].map(([startKey, group]) => {
                  const shown = group.slice(0, 3);
                  const rest = group.length - shown.length;
                  return (
                    <div
                      key={startKey}
                      className="pointer-events-none absolute left-1.5 z-20 flex items-center gap-0.5"
                      style={{ top: top(group[0]!.start) + 19 }}
                    >
                      {shown.map((w) => (
                        <span
                          key={w.friend.id}
                          title={`${w.friend.name} · ${fmtRange(w.start, w.end)}`}
                          className={cn(
                            "grid size-[19px] place-items-center rounded-full border border-card text-[9px] font-bold text-background shadow-sm",
                            w.intent === "maybe" && "opacity-70 ring-1 ring-dashed",
                          )}
                          style={{ background: `var(--chip-${w.friend.chip})` }}
                        >
                          {w.friend.initials}
                        </span>
                      ))}
                      {rest > 0 && (
                        <span className="grid h-[19px] min-w-[19px] place-items-center rounded-full border border-border bg-muted px-1 text-[9px] font-bold text-muted-foreground">
                          +{rest}
                        </span>
                      )}
                    </div>
                  );
                })}

                {/* scheduled sessions */}
                {sessions.map((s) => (
                  <div
                    key={s.id}
                    className="pointer-events-none absolute right-1.5 left-[38%] z-20 overflow-hidden rounded-xl border-2 border-foreground/70 bg-accent px-1.5 py-1 text-accent-foreground shadow-[var(--shadow-card)]"
                    style={{ top: top(s.start) + 2, minHeight: 26, height: hgt(s.start, s.end) - 4 }}
                  >
                    <p className="truncate font-display text-[11px] leading-tight font-extrabold">
                      🎮 {s.title}
                    </p>
                    <p className="truncate text-[10px] leading-tight font-semibold opacity-80">
                      {fmt(s.start)} · {t("lovable.grid.inCount", { count: s.occupancy })}
                    </p>
                  </div>
                ))}

                {/* poll options */}
                {polls.map((p) => (
                  <div
                    key={p.id}
                    className="pointer-events-none absolute right-1.5 left-[38%] z-20 overflow-hidden rounded-xl border-2 border-dashed border-foreground/45 bg-card/85 px-1.5 py-1"
                    style={{ top: top(p.start) + 2, minHeight: 26, height: hgt(p.start, p.end) - 4 }}
                  >
                    <p className="truncate font-display text-[11px] leading-tight font-extrabold">
                      ? {p.pollTitle}
                    </p>
                    <p className="truncate text-[10px] leading-tight font-semibold text-muted-foreground">
                      {t("lovable.grid.pollPill", { vote: p.voteLabel })}
                    </p>
                  </div>
                ))}

                {/* live drag preview */}
                {activeDrag && activeDrag.mode === "new" && (
                  <div
                    className="pointer-events-none absolute inset-x-1 z-30 rounded-xl border-2 border-primary bg-primary/25 px-2 py-1"
                    style={{
                      top: top(activeDrag.a),
                      height: Math.max(hgt(activeDrag.a, activeDrag.b), 16),
                    }}
                  >
                    <span className="font-display text-[11px] font-bold tabular-nums">
                      {fmtRange(activeDrag.a, activeDrag.b)}
                    </span>
                  </div>
                )}

                {/* now line */}
                {isToday && nowMinutes !== null && nowMinutes >= rangeStart && nowMinutes <= rangeEnd && (
                  <div
                    className="pointer-events-none absolute inset-x-0 z-40 flex items-center"
                    style={{ top: top(nowMinutes) }}
                  >
                    <span className="size-2 rounded-full bg-destructive" />
                    <span className="h-px flex-1 bg-destructive" />
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {/* Keyboard/AT layer: the painted columns above are pointer-driven, so
            the same 15-minute slots are also exposed as a real ARIA grid.
            It is transparent and pointer-transparent — focus, roles and
            accessible names only, no pixels of its own. */}
        <div
          aria-colcount={days.length + 1}
          aria-label={t("a11y.gridLabel")}
          aria-rowcount={rowCount}
          className="pointer-events-none absolute inset-0 grid"
          role="grid"
          style={{ gridTemplateRows: `repeat(${rowCount}, ${ROW_H}px)` }}
          onKeyDown={onCellKeyDown}
        >
          {Array.from({ length: rowCount }, (_, row) => (
            <div
              key={row}
              aria-rowindex={row + 1}
              className="grid gap-1.5"
              role="row"
              style={{ gridTemplateColumns: gridTemplate }}
            >
              <div aria-colindex={1} className="sr-only" role="rowheader">
                {fmt(minutesOfRow(row))}
              </div>
              {days.map((day, col) => {
                const covering = busyAt(day, row);
                const isFocus = col === focusCol && row === focusRow;
                return (
                  <div
                    key={day}
                    aria-colindex={col + 2}
                    aria-label={`${labels.dayNames[day]} ${fmt(minutesOfRow(row))}${
                      covering ? ` – ${t("a11y.busy")}` : ""
                    }`}
                    aria-selected={
                      keyAnchor !== null && keyAnchor.col === col
                        ? row >= Math.min(keyAnchor.row, focusRow) &&
                          row <= Math.max(keyAnchor.row, focusRow)
                        : undefined
                    }
                    className="rounded-sm outline-focus focus-visible:outline-2 focus-visible:outline-offset-1"
                    data-busy={covering ? "true" : undefined}
                    data-cell={`${col}:${row}`}
                    data-state={covering ? "busy" : "free"}
                    role="gridcell"
                    tabIndex={isFocus ? 0 : -1}
                    onFocus={() => setFocusCell({ col, row })}
                  />
                );
              })}
            </div>
          ))}
        </div>
      </div>

      <div aria-live="polite" className="sr-only" role="status">
        {liveMessage}
      </div>
    </div>
  );
}

function BandBlock({
  band,
  top,
  height,
  selected,
  onSelect,
  onHandleDown,
  onNudge,
  live,
  topOf,
  heightOf,
}: {
  band: Band;
  top: number;
  height: number;
  selected: boolean;
  onSelect: () => void;
  onHandleDown: (edge: "start" | "end", e: React.PointerEvent) => void;
  onNudge: (edge: "start" | "end", delta: number) => void;
  live: { start: number; end: number } | null;
  topOf: (m: number) => number;
  heightOf: (a: number, b: number) => number;
}) {
  const { t } = useTranslation("availability");
  const tOf = live ? topOf(live.start) : top;
  const h = live ? heightOf(live.start, live.end) : height;
  const start = live ? live.start : band.start;
  const end = live ? live.end : band.end;

  return (
    <div
      role="button"
      tabIndex={0}
      onPointerDown={(e) => {
        e.stopPropagation();
        onSelect();
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onSelect();
        }
      }}
      className={cn(
        "absolute inset-x-1 z-10 rounded-xl px-2 py-1 text-left transition-shadow",
        band.intent === "free"
          ? "bg-free text-free-foreground"
          : "bg-maybe text-maybe-foreground",
        band.intent === "maybe" && "border-2 border-dashed border-current/40",
        selected && "shadow-[var(--shadow-pop)] ring-2 ring-ring",
      )}
      style={{ top: tOf, height: Math.max(h, 18) }}
    >
      <span className="font-display text-[11px] leading-tight font-bold tabular-nums">
        {fmtRange(start, end)}
      </span>
      {h > 44 && (
        <span className="block text-[10px] leading-tight font-semibold opacity-90">
          {band.kind === "recurring" ? t("lovable.grid.weekly") : t("lovable.grid.oneTime")}
        </span>
      )}
      {selected && (
        <>
          <Handle
            position="top"
            onPointerDown={(e) => onHandleDown("start", e)}
            onNudge={(delta) => onNudge("start", delta)}
          />
          <Handle
            position="bottom"
            onPointerDown={(e) => onHandleDown("end", e)}
            onNudge={(delta) => onNudge("end", delta)}
          />
        </>
      )}
    </div>
  );
}

function Handle({
  position,
  onPointerDown,
  onNudge,
}: {
  position: "top" | "bottom";
  onPointerDown: (e: React.PointerEvent) => void;
  onNudge: (delta: number) => void;
}) {
  const { t } = useTranslation("availability");
  return (
    <button
      type="button"
      onPointerDown={onPointerDown}
      onKeyDown={(e) => {
        if (e.key === "ArrowUp" || e.key === "ArrowDown") {
          e.preventDefault();
          onNudge(e.key === "ArrowUp" ? -SLOT_MIN : SLOT_MIN);
          return;
        }
        // The handle is only a resize affordance — a native click on Enter or
        // Space would re-fire the band's own selection underneath it, and the
        // band's keydown handler is an ancestor of this button.
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          e.stopPropagation();
        }
      }}
      aria-label={position === "top" ? t("grid.resizeStart") : t("grid.resizeEnd")}
      className={cn(
        "absolute left-1/2 z-30 grid h-6 w-11 -translate-x-1/2 cursor-ns-resize touch-none place-items-center rounded-full border-2 border-ring bg-card shadow-[var(--shadow-card)]",
        position === "top" ? "-top-3" : "-bottom-3",
      )}
    >
      <GripHorizontal className="size-3.5 text-muted-foreground" />
    </button>
  );
}
