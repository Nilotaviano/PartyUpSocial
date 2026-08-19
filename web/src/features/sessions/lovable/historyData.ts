import { instantToFakeLocal } from '../../availability/lovable/zone'
import type { MaskedParticipant, SessionSummaryResponse } from './sessionData'

/**
 * View model for Sessions > History (D-221/D-222/D-223). `startTime` is
 * "fake local" exactly like `sessionData.ts`: produced by `instantToFakeLocal`
 * in the viewer's profile zone (D-038/D-046) and read back through
 * `Intl.DateTimeFormat` with `timeZone: 'UTC'`, so month grouping happens in
 * the viewer's zone rather than the browser's.
 *
 * Masking follows the wire contract: `displayName` is absent only for a
 * blocked pair (D-076) or a tombstoned account (D-092), while `avatar` is
 * absent for any non-friend. A player with no name never contributes a chip,
 * a search match, or any rendered text.
 */

export type HistoryPlayerView = {
  /** React key and `AvatarDisc` fill seed — ordinals are per-response, so they are session-scoped. */
  key: string
  /** `null` when the wire withheld the identity (D-076/D-092). */
  name: string | null
  emoji: string | null
  imgUrl: string | null
  masked: boolean
}

export type HistoryEntryView = {
  id: string
  title: string
  gameName: string | null
  /** Fake-local instant (D-046). */
  startTime: Date
  durationMinutes: number
  canceled: boolean
  organizedByViewer: boolean
  players: HistoryPlayerView[]
  /** `YYYY-MM` in the viewer's zone. */
  monthKey: string
}

export type HistoryMonthGroup = {
  key: string
  /** Fake-local first day of the month, for locale-aware label formatting. */
  monthStart: Date
  items: HistoryEntryView[]
}

export type HistoryFilter = {
  query: string
  game: string | null
  player: string | null
}

export const emptyHistoryFilter: HistoryFilter = { query: '', game: null, player: null }

function mapPlayer(sessionId: string, player: MaskedParticipant): HistoryPlayerView {
  const masked = player.displayName == null
  return {
    key: `${sessionId}:${player.ordinal}`,
    name: masked ? null : player.displayName!,
    emoji: player.avatar?.emoji ?? null,
    imgUrl: player.avatar?.imgUrl ?? null,
    masked,
  }
}

export function mapHistoryEntry(
  response: SessionSummaryResponse,
  timeZone: string,
  viewerId?: string | null,
): HistoryEntryView {
  const startTime = instantToFakeLocal(response.startTime, timeZone)
  return {
    id: response.id,
    title: response.title,
    gameName: response.game?.name ?? null,
    startTime,
    durationMinutes: Number(response.durationMinutes),
    canceled: response.status === 'canceled',
    organizedByViewer:
      response.viewerRelation === 'organizer' ||
      (viewerId != null && response.organizerId === viewerId),
    players: (response.players ?? []).map((player) => mapPlayer(response.id, player)),
    monthKey: monthKeyOf(startTime),
  }
}

export function monthKeyOf(fakeLocal: Date): string {
  return `${fakeLocal.getUTCFullYear()}-${String(fakeLocal.getUTCMonth() + 1).padStart(2, '0')}`
}

/** Month groups, newest month first, entries newest first inside each month (D-221). */
export function groupByMonth(entries: HistoryEntryView[]): HistoryMonthGroup[] {
  const groups = new Map<string, HistoryEntryView[]>()
  for (const entry of entries) {
    const bucket = groups.get(entry.monthKey)
    if (bucket) bucket.push(entry)
    else groups.set(entry.monthKey, [entry])
  }

  return [...groups.entries()]
    .sort(([left], [right]) => (left < right ? 1 : left > right ? -1 : 0))
    .map(([key, items]) => {
      const sorted = [...items].sort(
        (left, right) => right.startTime.getTime() - left.startTime.getTime(),
      )
      const [year, month] = key.split('-').map(Number)
      return { key, monthStart: new Date(Date.UTC(year, month - 1, 1)), items: sorted }
    })
}

function legibleNames(entry: HistoryEntryView): string[] {
  return entry.players.flatMap((player) => (player.name == null ? [] : [player.name]))
}

/**
 * D-223: a client-side narrowing affordance over the already-fetched page —
 * never a security boundary (visibility and D-076 masking are server-side).
 * Search, game chip and player chip compose with AND.
 */
export function filterHistory(
  entries: HistoryEntryView[],
  filter: HistoryFilter,
): HistoryEntryView[] {
  const query = filter.query.trim().toLocaleLowerCase()
  return entries.filter((entry) => {
    if (filter.game != null && entry.gameName !== filter.game) return false
    if (filter.player != null && !legibleNames(entry).includes(filter.player)) return false
    if (query.length === 0) return true
    const haystack = [entry.title, entry.gameName ?? '', ...legibleNames(entry)]
    return haystack.some((value) => value.toLocaleLowerCase().includes(query))
  })
}

/** Localized duration of a completed night — hours, minutes, or both. */
export function historyDurationLabel(
  minutes: number,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  if (hours > 0 && rest > 0) return t('history.duration.hoursMinutes', { hours, minutes: rest })
  if (hours > 0) return t('history.duration.hours', { hours })
  return t('history.duration.minutes', { minutes: rest })
}

function byFrequency(counts: Map<string, number>): string[] {
  return [...counts.entries()]
    .sort(([leftName, left], [rightName, right]) =>
      right - left || leftName.localeCompare(rightName),
    )
    .map(([name]) => name)
}

/** Game chip options, most-played first (D-223). */
export function gameOptions(entries: HistoryEntryView[]): string[] {
  const counts = new Map<string, number>()
  for (const entry of entries) {
    if (entry.gameName == null) continue
    counts.set(entry.gameName, (counts.get(entry.gameName) ?? 0) + 1)
  }
  return byFrequency(counts)
}

/**
 * Played-with chip options: legible names across the loaded completed entries,
 * most-frequent first. The viewer's own name is never offered as a chip.
 */
export function playerOptions(
  entries: HistoryEntryView[],
  viewerName?: string | null,
  max = 12,
): string[] {
  const counts = new Map<string, number>()
  for (const entry of entries) {
    if (entry.canceled) continue
    for (const name of new Set(legibleNames(entry))) {
      if (viewerName != null && name === viewerName) continue
      counts.set(name, (counts.get(name) ?? 0) + 1)
    }
  }
  return byFrequency(counts).slice(0, max)
}
