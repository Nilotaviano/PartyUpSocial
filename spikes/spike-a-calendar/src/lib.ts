export const SLOTS_PER_DAY = 96; // 15-minute slots (D-132)
export const SLOT_MINUTES = 15;

export const DAYS = [
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
  "Sunday",
] as const;

/** One availability block on the (single, current) week. endSlot is exclusive. */
export interface Block {
  id: string;
  day: number; // 0 = Monday .. 6 = Sunday
  startSlot: number; // 0..95
  endSlot: number; // 1..96, exclusive
}

export function slotToTime(slot: number): string {
  const h = Math.floor(slot / 4);
  const m = (slot % 4) * SLOT_MINUTES;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

export function rangeLabel(day: number, startSlot: number, endSlot: number): string {
  return `${DAYS[day]} ${slotToTime(startSlot)} to ${slotToTime(endSlot)}`;
}

/** Monday 00:00 (local) of the current week. */
export function weekStart(): Date {
  const now = new Date();
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const dayIdx = (d.getDay() + 6) % 7; // Monday = 0
  d.setDate(d.getDate() - dayIdx);
  return d;
}

export function slotToDate(week: Date, day: number, slot: number): Date {
  const d = new Date(week);
  d.setDate(d.getDate() + day);
  d.setHours(Math.floor(slot / 4), (slot % 4) * SLOT_MINUTES, 0, 0);
  return d;
}

export function dateToDaySlot(d: Date): { day: number; slot: number } {
  return {
    day: (d.getDay() + 6) % 7,
    slot: d.getHours() * 4 + Math.floor(d.getMinutes() / SLOT_MINUTES),
  };
}

export function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v));
}

export function newId(): string {
  return crypto.randomUUID();
}
