/**
 * The grid engine's feature-agnostic vocabulary.
 *
 * Nothing here knows about availability rules, poll candidates or votes: a
 * consumer describes its editable rows as {@link GridBlock}s in
 * minutes-from-midnight, and gets pixels back from {@link createGridGeometry}
 * and gestures back from `useGridGestures`. Availability-specific types
 * (`AvailabilityRule`, `Band`) stay in `partyup-data.ts` and must not leak in.
 */

/** One editable row of a day column, in minutes-from-midnight of that day. */
export type GridBlock = {
  id: string;
  day: number;
  startMin: number;
  endMin: number;
  /**
   * Presentation hints the engine carries but never interprets — the consumer
   * decides what a variant or a colour means when it paints the block.
   */
  variant?: string;
  color?: string;
  label?: string;
  sublabel?: string;
};

/** Which edge of a block a resize drag is moving. */
export type GridEdge = "start" | "end";

/** Viewport coordinates a create gesture ended at, for anchoring a popover. */
export type GridPoint = { x: number; y: number };

export type GridDrag =
  | {
      mode: "create";
      day: number;
      /** The minute the press landed on; the range grows either side of it. */
      anchor: number;
      a: number;
      b: number;
      /**
       * Sticky: the range reached at least one minimum duration at some point.
       * Consumers that treat a press-without-drag as a discrete tap read this
       * to tell the two apart; the commit rule itself is the final extent.
       */
      painted: boolean;
    }
  | {
      mode: "move";
      day: number;
      blockId: string;
      a: number;
      b: number;
      /** Where the block sat when it was grabbed — a drag ending here wrote nothing. */
      originStart: number;
      originEnd: number;
    }
  | {
      mode: "resize";
      day: number;
      blockId: string;
      edge: GridEdge;
      a: number;
      b: number;
      originStart: number;
      originEnd: number;
    };

export const DEFAULT_STEP_MIN = 15;
/** What a long-press paint starts out covering before the finger moves. */
export const DEFAULT_TOUCH_CREATE_MIN = 30;

export function snapMinutes(mins: number, step: number) {
  return Math.round(mins / step) * step;
}

export function clampMinutes(value: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, value));
}

export type GridGeometrySpec = {
  rangeStart: number;
  rangeEnd: number;
  /** Minutes one painted row of the column represents. */
  slotMin: number;
  /** Pixels one `slotMin` occupies. */
  rowH: number;
  /**
   * Minutes one *addressable* row of the ARIA cell layer represents, when the
   * keyboard lattice is coarser than the painted one (a poll grid places on 30,
   * paints on 15). Defaults to `slotMin`.
   */
  rowStepMin?: number;
};

export type GridGeometry = {
  totalMin: number;
  /** Height of a full day column, in pixels. */
  pixelHeight: number;
  /** Number of rows in the ARIA cell layer. */
  rowCount: number;
  /** Pixel offset of a minute from the top of a day column. */
  top: (minute: number) => number;
  /** Pixel height of a minute range. */
  span: (a: number, b: number) => number;
  /** Minutes of an ARIA-layer row index. */
  minutesOfRow: (row: number) => number;
  /**
   * Gridlines as data. They are painted as real elements rather than a
   * `repeating-linear-gradient` because iOS Safari rounds each gradient tile
   * independently at fractional device pixel ratios, so the painted spacing
   * drifts away from the hour labels and bands down the column.
   */
  lines: { m: number; hour: boolean }[];
};

export function createGridGeometry({
  rangeStart,
  rangeEnd,
  slotMin,
  rowH,
  rowStepMin,
}: GridGeometrySpec): GridGeometry {
  const totalMin = rangeEnd - rangeStart;
  const rowStep = rowStepMin ?? slotMin;
  const lines: { m: number; hour: boolean }[] = [];
  for (let m = rangeStart; m < rangeEnd; m += slotMin) lines.push({ m, hour: m % 60 === 0 });
  return {
    totalMin,
    pixelHeight: (totalMin / slotMin) * rowH,
    rowCount: Math.max(1, Math.round(totalMin / rowStep)),
    top: (minute: number) => ((minute - rangeStart) / slotMin) * rowH,
    span: (a: number, b: number) => ((b - a) / slotMin) * rowH,
    minutesOfRow: (row: number) => rangeStart + row * rowStep,
    lines,
  };
}
