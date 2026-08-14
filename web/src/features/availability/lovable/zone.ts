/**
 * The zoned-conversion boundary (D-046). Every `Date` below the shell is
 * "fake local": its UTC accessors hold the viewer's profile-zone wall clock,
 * so the grid can do plain arithmetic without ever touching a real zone.
 * Conversion happens only here, through `Intl.DateTimeFormat` — no timezone
 * library is in the web write set.
 */
export const DAY_MS = 24 * 60 * 60 * 1000;

export function zonedOffsetMinutes(utcMs: number, timeZone: string): number {
  const formatter = new Intl.DateTimeFormat("en-US", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
  const parts = Object.fromEntries(
    formatter.formatToParts(new Date(utcMs)).map((part) => [part.type, part.value]),
  );
  const hour = parts.hour === "24" ? 0 : Number(parts.hour);
  const asUtc = Date.UTC(
    Number(parts.year),
    Number(parts.month) - 1,
    Number(parts.day),
    hour,
    Number(parts.minute),
    Number(parts.second),
  );
  return (asUtc - utcMs) / 60_000;
}

export function instantToFakeLocal(iso: string, timeZone: string): Date {
  const utcMs = new Date(iso).getTime();
  const offset = zonedOffsetMinutes(utcMs, timeZone);
  return new Date(utcMs + offset * 60_000);
}

export function fakeLocalToInstant(fakeLocal: Date, timeZone: string): string {
  const guessUtc = Date.UTC(
    fakeLocal.getUTCFullYear(),
    fakeLocal.getUTCMonth(),
    fakeLocal.getUTCDate(),
    fakeLocal.getUTCHours(),
    fakeLocal.getUTCMinutes(),
    0,
    0,
  );
  const offset = zonedOffsetMinutes(guessUtc, timeZone);
  const instantMs = guessUtc - offset * 60_000;
  const correctedOffset = zonedOffsetMinutes(instantMs, timeZone);
  return new Date(guessUtc - correctedOffset * 60_000).toISOString();
}

export function isoDateOf(fakeLocal: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${fakeLocal.getUTCFullYear()}-${pad(fakeLocal.getUTCMonth() + 1)}-${pad(
    fakeLocal.getUTCDate(),
  )}`;
}

export function hhmmOf(fakeLocal: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${pad(fakeLocal.getUTCHours())}:${pad(fakeLocal.getUTCMinutes())}`;
}

export function weekStartsOnIso(locale: string): number {
  try {
    const withWeekInfo = new Intl.Locale(locale) as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number };
      weekInfo?: { firstDay: number };
    };
    const info = withWeekInfo.getWeekInfo?.() ?? withWeekInfo.weekInfo;
    if (info?.firstDay) return info.firstDay;
  } catch {
    // Unsupported locale/environment — the ISO default below still holds.
  }
  return 1;
}

export function startOfLocaleWeek(reference: Date, firstDayIso: number): Date {
  const jsFirstDay = firstDayIso % 7; // ISO 7 (Sunday) -> JS 0
  const diff = (reference.getUTCDay() - jsFirstDay + 7) % 7;
  const start = new Date(reference);
  start.setUTCDate(start.getUTCDate() - diff);
  start.setUTCHours(0, 0, 0, 0);
  return start;
}
