import { Ban, CircleHelp, ThumbsUp } from 'lucide-react'
import type { components } from '../../../api/schema'
import { instantToFakeLocal } from '../../availability/lovable/zone'

/**
 * View types and mapper for the ported poll UI (S1 of the verbatim port).
 * `mapPollDetail`/`mapFinalizationPreview` are the zoned-conversion boundary
 * (D-046): every `Date` on the view types below is "fake local", produced by
 * `instantToFakeLocal` and read back through `Intl.DateTimeFormat` with
 * `timeZone: 'UTC'`, exactly like `../../availability/lovable/calendarData.ts`.
 */

export type PollDetailResponse = components['schemas']['PollDetailResponse']
export type PollOptionResponse = components['schemas']['PollOptionResponse']
export type PollRosterEntryResponse = components['schemas']['PollRosterEntryResponse']
export type PollFinalizationPreviewResponse =
  components['schemas']['PollFinalizationPreviewResponse']
export type PollFinalizationPreviewRosterEntryResponse =
  components['schemas']['PollFinalizationPreviewRosterEntryResponse']

/** Wire values are the backend's camelCase enum serialization (PollEnums.cs). */
export type PollStatus = 'polling' | 'closed' | 'finalized' | 'expired' | 'canceled'
export type VoteValue = 'available' | 'maybe' | 'unavailable'

/**
 * A roster entry, resolved for display. `name` already carries the D-076
 * anonymous-label fallback (mirrors `Friend.name` in
 * `../../availability/lovable/calendarData.ts`) so presentational components
 * never need a translation hook of their own.
 */
export type Voter = {
  /** `PollRosterEntryResponse.participationId` — stable within this poll. */
  id: string
  name: string
  /** Avatar-fill hash seed (`AvatarDisc`'s `seed`); the participation id. */
  seed: string
  emoji: string | null
  imgUrl: string | null
  masked: boolean
  isActive: boolean
}

export type PollOptionT = {
  id: string
  /** Fake-local instant (D-046) — a poll option carries no duration (spec §7.3-1). */
  start: Date
  availableCount: number
  maybeCount: number
  unavailableCount: number
  viewerVote: VoteValue | null
  /**
   * Per-category voter chips, ordered by first-submitted-response ascending.
   * Display-only (chips next to the count) — `availableCount`/`maybeCount`/
   * `unavailableCount` above remain the authoritative tally even if a list's
   * length disagrees with its count (A-076: never re-tally from these lists).
   */
  availableVoters: Voter[]
  maybeVoters: Voter[]
  unavailableVoters: Voter[]
  /**
   * D-196: who suggested this option, masked through the identical assigner pass as the voter
   * lists (`resolveIdentity`) — `null` for an organizer-created option (no attribution).
   */
  suggestedBy: { name: string; masked: boolean } | null
}

export function optionDate(option: PollOptionT) {
  return option.start
}

/** `formatters.day` must be built with `timeZone: 'UTC'` over fake-local dates (D-046). */
export function optionDayLabel(option: PollOptionT, formatters: PollFormatters) {
  return formatters.day.format(option.start)
}

/** `formatters.time` must be built with `timeZone: 'UTC'` over fake-local dates (D-046). */
export function optionRange(option: PollOptionT, durationMin: number, formatters: PollFormatters) {
  const end = new Date(option.start.getTime() + durationMin * 60_000)
  return `${formatters.time.format(option.start)}–${formatters.time.format(end)}`
}

export type PollFormatters = {
  day: Intl.DateTimeFormat
  time: Intl.DateTimeFormat
}

/**
 * Icon + fill per vote value — the D-139 half of the vote-state coding. Lives
 * here rather than in `./PollBits` (a component file: `react-refresh/only-
 * export-components` forbids that file from exporting anything else).
 */
export const VOTE_META: Record<VoteValue, { icon: typeof ThumbsUp; cls: string }> = {
  available: {
    icon: ThumbsUp,
    cls: 'bg-free text-free-foreground border-free-foreground/40',
  },
  maybe: {
    icon: CircleHelp,
    cls: 'bg-maybe text-maybe-foreground border-maybe-foreground/40',
  },
  unavailable: {
    icon: Ban,
    cls: 'bg-busy text-busy-foreground border-border',
  },
}

/** A candidate time slot on the `CandidateGrid` (day index + minute-of-day). */
export type Candidate = { day: number; start: number }

/**
 * Same reasoning as `VOTE_META` above: `candidateKey` lives here, not in the
 * component file `./CandidateGrid`, purely to satisfy `react-refresh/only-
 * export-components` (a component file may not export anything else).
 */
export function candidateKey(c: Candidate) {
  return `${c.day}-${c.start}`
}

export type Tally = {
  available: number
  maybe: number
  unavailable: number
  pending: number
}

/**
 * D-052 display ordering rests on the counts the server already aggregates
 * per option — there is no per-voter vote list on the wire to re-tally from
 * (A-076: the client never re-expresses that as its own count).
 * `totalActiveVoters` (roster entries with `isActive`, excluding the
 * organizer if the caller wants that convention) is optional; omitted, the
 * tally simply reports zero pending.
 */
export function tallyFor(option: PollOptionT, totalActiveVoters = 0): Tally {
  const responded = option.availableCount + option.maybeCount + option.unavailableCount
  return {
    available: option.availableCount,
    maybe: option.maybeCount,
    unavailable: option.unavailableCount,
    pending: Math.max(0, totalActiveVoters - responded),
  }
}

/** D-052: rank by Available count, then Maybe count, then earliest start. */
export function rankedOptionIds(options: PollOptionT[]): string[] {
  return [...options]
    .sort((a, b) => {
      if (b.availableCount !== a.availableCount) return b.availableCount - a.availableCount
      if (b.maybeCount !== a.maybeCount) return b.maybeCount - a.maybeCount
      return a.start.getTime() - b.start.getTime()
    })
    .map((o) => o.id)
}

/**
 * A finalization-preview seat for one option. `status` is the raw
 * `ParticipationStatus` wire string (`"going"` | `"maybe"` | `"declined"` |
 * `"pending"`) passed through unchanged (A-076) — the allocation rule itself
 * lives only in `Poll.AllocateRoster` (Domain), never re-expressed here.
 */
export type Seat = {
  voterId: string
  name: string
  seed: string
  emoji: string | null
  imgUrl: string | null
  masked: boolean
  status: string
  seated: boolean
}

export type FinalizationOption = {
  optionId: string
  meetsMinimum: boolean
  seats: Seat[]
}

export type PollLabels = {
  anonymousVoter: string
}

function resolveIdentity(
  id: string,
  displayName: string | null,
  avatar: { emoji?: string | null; imgUrl?: string | null } | null | undefined,
  labels: PollLabels,
) {
  const masked = displayName == null
  return {
    seed: id,
    name: masked ? labels.anonymousVoter : displayName,
    emoji: masked ? null : (avatar?.emoji ?? null),
    imgUrl: masked ? null : (avatar?.imgUrl ?? null),
    masked,
  }
}

export function statusLabelKey(status: PollStatus): string {
  switch (status) {
    case 'polling':
      return 'status.polling'
    case 'closed':
      return 'status.closed'
    case 'finalized':
      return 'status.finalized'
    case 'expired':
      return 'status.expired'
    case 'canceled':
      return 'status.canceled'
  }
}

/**
 * Reverses `PollCreatePage`'s `platformWireValues` map for display — the
 * wire value travels as the backend's camelCase enum serialization
 * (`steam`/`playStation`/`xbox`/`notApplicable`), never the create form's
 * localized "Other" label, so a bare `poll.platform` would otherwise leak
 * `notApplicable` straight onto the page (TODO.md 2026-08-15).
 */
export function platformLabel(platform: string, t: (key: string) => string): string {
  switch (platform) {
    case 'steam':
      return 'Steam'
    case 'playStation':
      return 'PlayStation'
    case 'xbox':
      return 'Xbox'
    case 'notApplicable':
      return t('create.platformOther')
    default:
      return platform
  }
}

export function formatCountdown(minutes: number) {
  if (minutes <= 0) return '0h 00m'
  const h = Math.floor(minutes / 60)
  const m = Math.floor(minutes % 60)
  return `${h}h ${String(m).padStart(2, '0')}m`
}

export type MappedPoll = {
  id: string
  organizerId: string
  title: string
  game: { name: string; steamAppId: number | string | null } | null
  platform: string
  durationMinutes: number
  notes: string
  minAttendance: number
  maxCapacity: number | null
  visibility: string
  /** D-206 provenance — see `ShareTag` (`features/groups`). */
  viaGroup: { id: string; name: string } | null
  sharedWithGroup: boolean
  deadline: Date
  selectionWindowEndsAt: Date
  status: PollStatus
  createdAt: Date
  selectedOptionId: string | null
  createdSessionId: string | null
  options: PollOptionT[]
  roster: Voter[]
  muted: boolean
}

export function mapPollDetail(
  response: PollDetailResponse | undefined,
  timeZone: string,
  labels: PollLabels,
): MappedPoll | null {
  if (!response) return null
  return {
    id: response.id,
    organizerId: response.organizerId,
    title: response.title,
    game: response.game ? { name: response.game.name, steamAppId: response.game.steamAppId } : null,
    platform: response.platform,
    durationMinutes: Number(response.durationMinutes),
    notes: response.notes,
    minAttendance: Number(response.minAttendance),
    maxCapacity: response.maxCapacity == null ? null : Number(response.maxCapacity),
    visibility: response.visibility,
    viaGroup: response.viaGroup,
    sharedWithGroup: response.sharedWithGroup,
    deadline: instantToFakeLocal(response.deadline, timeZone),
    selectionWindowEndsAt: instantToFakeLocal(response.selectionWindowEndsAt, timeZone),
    status: response.status as PollStatus,
    createdAt: instantToFakeLocal(response.createdAt, timeZone),
    selectedOptionId: response.selectedOptionId,
    createdSessionId: response.createdSessionId,
    options: response.options.map((option) => mapOption(option, timeZone, labels)),
    roster: response.roster.map((entry) => mapRosterEntry(entry, labels)),
    muted: response.muted,
  }
}

function mapOption(
  option: PollOptionResponse,
  timeZone: string,
  labels: PollLabels,
): PollOptionT {
  return {
    id: option.id,
    start: instantToFakeLocal(option.startTime, timeZone),
    availableCount: Number(option.availableCount),
    maybeCount: Number(option.maybeCount),
    unavailableCount: Number(option.unavailableCount),
    viewerVote: (option.viewerVote as VoteValue | null) ?? null,
    availableVoters: option.availableVoters.map((entry) => mapRosterEntry(entry, labels)),
    maybeVoters: option.maybeVoters.map((entry) => mapRosterEntry(entry, labels)),
    unavailableVoters: option.unavailableVoters.map((entry) => mapRosterEntry(entry, labels)),
    suggestedBy: option.suggester
      ? {
          name: resolveIdentity(
            option.suggester.participationId,
            option.suggester.displayName,
            option.suggester.avatar,
            labels,
          ).name,
          masked: option.suggester.displayName == null,
        }
      : null,
  }
}

function mapRosterEntry(entry: PollRosterEntryResponse, labels: PollLabels): Voter {
  const identity = resolveIdentity(entry.participationId, entry.displayName, entry.avatar, labels)
  return {
    id: entry.participationId,
    isActive: entry.isActive,
    ...identity,
  }
}

/**
 * The real seating comes from `GetPollFinalizationPreview`, never a
 * client-side re-implementation of `Poll.AllocateRoster` (A-076). One
 * `FinalizationOption` per candidate time — seating differs by option, so a
 * single flat `Seat[]` across every option would lose which time each seat
 * belongs to.
 */
export function mapFinalizationPreview(
  response: PollFinalizationPreviewResponse | undefined,
  labels: PollLabels,
): FinalizationOption[] {
  return (response?.options ?? []).map((option) => ({
    optionId: option.optionId,
    meetsMinimum: option.meetsMinimum,
    seats: option.roster.map((entry, index) =>
      mapFinalizationSeat(entry, labels, option.optionId, index),
    ),
  }))
}

function mapFinalizationSeat(
  entry: PollFinalizationPreviewRosterEntryResponse,
  labels: PollLabels,
  optionId: string,
  index: number,
): Seat {
  // D-076: a masked entry carries both `userId` and `guestIdentityId` as null
  // (the server withholds real identity), so a per-option/per-index fallback
  // is required here — otherwise every masked seat on this option would share
  // the id "", collapsing their React keys and `AvatarDisc` seeds together.
  const id = entry.userId ?? entry.guestIdentityId ?? `masked-${optionId}-${index}`
  const identity = resolveIdentity(id, entry.displayName, entry.avatar, labels)
  return {
    voterId: id,
    status: entry.status,
    seated: !entry.isWaitlisted,
    ...identity,
  }
}
