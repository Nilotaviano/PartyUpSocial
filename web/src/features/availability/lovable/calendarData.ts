import type { AvailabilitySeriesView, CalendarView } from '../availabilityQueries'
import type {
  AvailabilityRule,
  Friend,
  GameSession,
  MergedSpan,
  PollOption,
} from './partyup-data'
import { DAY_MS, instantToFakeLocal } from './zone'

/**
 * What a grid/panel rule id addresses on the wire. A one-time block is its own
 * id; a series occurrence is the (series, date) pair. `start`/`end` are the
 * instants the server currently holds, so an undo can restore them exactly.
 */
export type RuleTarget =
  | { kind: 'block'; blockId: string; start: string; end: string }
  | {
      kind: 'occurrence'
      seriesId: string
      occurrenceDate: string
      start: string
      end: string
      series: AvailabilitySeriesView | null
    }

export type PollGroup = { pollId: string; title: string; options: PollOption[] }

export type MappedCalendar = {
  rules: AvailabilityRule[]
  merged: MergedSpan[]
  targets: Map<string, RuleTarget>
  friends: Friend[]
  sessions: GameSession[]
  polls: PollOption[]
  pollGroups: PollGroup[]
}

export type MappingLabels = {
  anonymousFriend: string
  voteLabel: (vote: string | null) => string
}

/** Minutes-from-midnight segments of a fake-local range, clipped to the week. */
function segmentsInWeek(start: Date, end: Date, weekStart: Date) {
  const out: { day: number; start: number; end: number }[] = []
  const weekStartMs = weekStart.getTime()
  const weekEndMs = weekStartMs + 7 * DAY_MS
  let cursor = Math.max(start.getTime(), weekStartMs)
  const stop = Math.min(end.getTime(), weekEndMs)
  while (cursor < stop) {
    const day = Math.floor((cursor - weekStartMs) / DAY_MS)
    const dayStart = weekStartMs + day * DAY_MS
    const segmentEnd = Math.min(stop, dayStart + DAY_MS)
    out.push({
      day,
      start: Math.round((cursor - dayStart) / 60_000),
      end: Math.round((segmentEnd - dayStart) / 60_000),
    })
    cursor = segmentEnd
  }
  return out
}

export function mapCalendar(
  view: CalendarView | undefined,
  weekStart: Date,
  timeZone: string,
  labels: MappingLabels,
): MappedCalendar {
  const rules: AvailabilityRule[] = []
  const targets = new Map<string, RuleTarget>()

  const seriesById = new Map<string, AvailabilitySeriesView>()
  for (const series of view?.myAvailability.series ?? []) {
    seriesById.set(series.seriesId, series)
  }

  for (const occurrence of view?.myAvailability.occurrences ?? []) {
    const start = instantToFakeLocal(occurrence.startTime, timeZone)
    const end = instantToFakeLocal(occurrence.endTime, timeZone)
    const isBlock = occurrence.origin.kind === 'block'
    const wireId = isBlock
      ? (occurrence.origin.blockId ?? '')
      : `${occurrence.origin.seriesId}:${occurrence.origin.occurrenceDate}`
    if (!wireId) continue

    const target: RuleTarget = isBlock
      ? {
          kind: 'block',
          blockId: occurrence.origin.blockId ?? '',
          start: occurrence.startTime,
          end: occurrence.endTime,
        }
      : {
          kind: 'occurrence',
          seriesId: occurrence.origin.seriesId ?? '',
          occurrenceDate: occurrence.origin.occurrenceDate ?? '',
          start: occurrence.startTime,
          end: occurrence.endTime,
          series: seriesById.get(occurrence.origin.seriesId ?? '') ?? null,
        }

    segmentsInWeek(start, end, weekStart).forEach((segment, index) => {
      const ruleId = index === 0 ? wireId : `${wireId}#${segment.day}`
      rules.push({
        id: ruleId,
        kind: isBlock ? 'oneoff' : 'recurring',
        days: [segment.day],
        start: segment.start,
        end: segment.end,
        // The domain publishes availability as a single free intent — there is
        // no "maybe" own-availability state to map (DOMAIN_MODEL.md §5).
        intent: 'free',
      })
      targets.set(ruleId, target)
    })
  }

  // D-101/A-076: the busy painting follows the server's merge verbatim. It is
  // deliberately not re-derived from `occurrences` — the two can differ and the
  // server's answer wins.
  const merged: MergedSpan[] = (view?.myAvailability.merged ?? []).flatMap((range) =>
    segmentsInWeek(
      instantToFakeLocal(range.start, timeZone),
      instantToFakeLocal(range.end, timeZone),
      weekStart,
    ),
  )

  const friends: Friend[] = (view?.friendAvailability ?? []).map((entry) => {
    const ordinal = Number(entry.owner.ordinal)
    const name = entry.owner.displayName ?? labels.anonymousFriend
    // D-076: a null display name is the wire's masking signal. The server already
    // withholds the avatar on that same row, so this only mirrors the decision.
    const masked = entry.owner.displayName == null
    return {
      id: `friend-${ordinal}`,
      name,
      emoji: masked ? null : (entry.owner.avatar?.emoji ?? null),
      imgUrl: masked ? null : (entry.owner.avatar?.imgUrl ?? null),
      masked,
      windows: entry.ranges.flatMap((range) =>
        segmentsInWeek(
          instantToFakeLocal(range.start, timeZone),
          instantToFakeLocal(range.end, timeZone),
          weekStart,
        ).map((segment) => ({ ...segment, intent: 'free' as const })),
      ),
    }
  })

  const sessions: GameSession[] = (view?.sessions ?? []).flatMap((session) => {
    const start = instantToFakeLocal(session.startTime, timeZone)
    const end = instantToFakeLocal(
      new Date(
        new Date(session.startTime).getTime() + Number(session.durationMinutes) * 60_000,
      ).toISOString(),
      timeZone,
    )
    const [segment] = segmentsInWeek(start, end, weekStart)
    if (!segment) return []
    return [
      {
        id: session.id,
        title: session.title,
        day: segment.day,
        start: segment.start,
        end: segment.end,
        occupancy: Number(session.activeOccupancy),
      },
    ]
  })

  const pollGroups: PollGroup[] = (view?.pollOptions ?? []).map((poll) => ({
    pollId: poll.pollId,
    title: poll.title,
    options: poll.options.flatMap((option) => {
      const start = instantToFakeLocal(option.startTime, timeZone)
      // Spec §7.3-1: a poll option is an instant with no duration. The pill
      // still needs a height, so it is painted as a one-hour block.
      const end = new Date(start.getTime() + 60 * 60_000)
      const [segment] = segmentsInWeek(start, end, weekStart)
      if (!segment) return []
      return [
        {
          id: option.optionId,
          pollTitle: poll.title,
          day: segment.day,
          start: segment.start,
          end: segment.end,
          voteLabel: labels.voteLabel(option.ownVote),
        },
      ]
    }),
  }))

  return {
    rules,
    merged,
    targets,
    friends,
    sessions,
    polls: pollGroups.flatMap((group) => group.options),
    pollGroups,
  }
}
