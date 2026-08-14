export const SLOT_MIN = 15;
export const ROW_H = 15; // px per 15 minutes -> 60px per hour

export function fmt(mins: number) {
  const m = ((mins % 1440) + 1440) % 1440;
  const h = Math.floor(m / 60);
  const mm = m % 60;
  return `${String(mins >= 1440 ? 24 : h).padStart(2, "0")}:${String(mm).padStart(2, "0")}`;
}

export function fmtRange(a: number, b: number) {
  return `${fmt(a)}–${fmt(b)}`;
}

export function snap(mins: number) {
  return Math.round(mins / SLOT_MIN) * SLOT_MIN;
}

export function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}

export type AvailabilityRule = {
  id: string;
  /** recurring rules apply to every listed weekday; one-off rules use a single day */
  kind: "recurring" | "oneoff";
  days: number[];
  start: number;
  end: number;
  intent: "free" | "maybe";
};

/** One server-merged busy span, in minutes-from-midnight of a week day. */
export type MergedSpan = { day: number; start: number; end: number };

export type Friend = {
  id: string;
  name: string;
  initials: string;
  chip: 1 | 2 | 3 | 4 | 5;
  /** D-188 avatar glyph; null for a picture avatar and for a masked owner. */
  emoji: string | null;
  /** D-191 proxy URL, never an upstream one; null unless the avatar is external. */
  imgUrl: string | null;
  /**
   * D-076: the owner arrived without a readable display name, so the disc renders
   * the neutral masked treatment. The server already withholds the avatar here —
   * this flag keeps the client from inventing one from the anonymous label.
   */
  masked: boolean;
  windows: { day: number; start: number; end: number; intent: "free" | "maybe" }[];
};

export type GameSession = {
  id: string;
  title: string;
  day: number;
  start: number;
  end: number;
  occupancy: number;
};

export type PollOption = {
  id: string;
  pollTitle: string;
  day: number;
  start: number;
  end: number;
  voteLabel: string;
};

/**
 * Weekday and date labels for the displayed week, produced once per week by
 * the shell: every label below the shell is locale-formatted from the real
 * week rather than a module constant, so nothing here can drift from the
 * week the grid is actually showing.
 */
export type WeekLabels = {
  /** Seven short weekday names, index 0 = the first day of the shown week. */
  dayNames: string[];
  /** Seven long weekday names, same indexing. */
  dayLongNames: string[];
  /** Seven "10 Aug"-style dates, same indexing. */
  dayDates: string[];
};

export function ruleLabel(rule: AvailabilityRule, labels: WeekLabels) {
  if (rule.kind === "recurring") {
    const days = rule.days.map((d) => labels.dayNames[d] ?? "").join(" & ");
    return `${days} · ${fmtRange(rule.start, rule.end)}`;
  }
  const d = rule.days[0] ?? 0;
  return `${labels.dayNames[d] ?? ""} ${labels.dayDates[d] ?? ""} · ${fmtRange(rule.start, rule.end)}`;
}

export type Band = {
  ruleId: string;
  day: number;
  start: number;
  end: number;
  intent: "free" | "maybe";
  kind: AvailabilityRule["kind"];
};

export function bandsForWeek(rules: AvailabilityRule[]): Band[] {
  const out: Band[] = [];
  for (const r of rules) {
    for (const d of r.days) {
      out.push({
        ruleId: r.id,
        day: d,
        start: r.start,
        end: r.end,
        intent: r.intent,
        kind: r.kind,
      });
    }
  }
  return out;
}
