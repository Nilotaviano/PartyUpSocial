import type { components } from '../../../api/schema'
import { instantToFakeLocal } from '../../availability/lovable/zone'

/**
 * View types and mapper for the ported session UI (S1 of the sessions
 * verbatim-language skin). `mapSessionSummary` is the zoned-conversion
 * boundary (D-046): `startTime` below is "fake local", produced by
 * `instantToFakeLocal` and read back through `Intl.DateTimeFormat` with
 * `timeZone: 'UTC'`, exactly like `../../polls/lovable/pollData.ts` and
 * `../../availability/lovable/calendarData.ts`.
 *
 * Kept minimal per S1's scope: only the fields the list card needs. A detail
 * mapper (roster, join requests, reminder, etc.) is later slices' to add.
 */

export type SessionSummaryResponse = components['schemas']['SessionSummaryResponse']
export type MaskedParticipant = components['schemas']['MaskedParticipant']

/** Wire values are the backend's camelCase enum serialization (SessionEnums.cs). */
export type SessionStatus = 'unconfirmed' | 'confirmed' | 'completed' | 'expired' | 'canceled'

export type OrganizerIdentity = {
  /** Avatar-fill hash seed (`AvatarDisc`'s `seed`) — the session's organizerId, or a
   * per-session fallback when the organizer is masked (organizerId travels as null). */
  seed: string
  name: string
  emoji: string | null
  imgUrl: string | null
  masked: boolean
}

export type SessionLabels = {
  anonymousOrganizer: string
}

export type MappedSessionSummary = {
  id: string
  title: string
  /** The raw wire status, widened to `SessionStatus` for known values (`StatusPill` renders
   * nothing for a status this build doesn't recognize, same posture as `SessionStatusChip`). */
  status: SessionStatus | string
  /** Fake-local instant (D-046). */
  startTime: Date
  durationMinutes: number
  platform: string
  game: { name: string; steamAppId: number | string | null } | null
  isPrivate: boolean
  maxCapacity: number | null
  activeOccupancy: number
  organizer: OrganizerIdentity
  viewerRelation: string
}

function resolveOrganizer(
  sessionId: string,
  organizerId: string | null,
  organizer: MaskedParticipant,
  labels: SessionLabels,
): OrganizerIdentity {
  const masked = organizer.displayName == null
  return {
    seed: organizerId ?? `masked-organizer-${sessionId}`,
    name: masked ? labels.anonymousOrganizer : organizer.displayName!,
    emoji: masked ? null : (organizer.avatar?.emoji ?? null),
    imgUrl: masked ? null : (organizer.avatar?.imgUrl ?? null),
    masked,
  }
}

export function mapSessionSummary(
  response: SessionSummaryResponse,
  timeZone: string,
  labels: SessionLabels,
): MappedSessionSummary {
  return {
    id: response.id,
    title: response.title,
    status: response.status,
    startTime: instantToFakeLocal(response.startTime, timeZone),
    durationMinutes: Number(response.durationMinutes),
    platform: response.platform,
    game: response.game ? { name: response.game.name, steamAppId: response.game.steamAppId } : null,
    isPrivate: response.isPrivate,
    maxCapacity: response.maxCapacity == null ? null : Number(response.maxCapacity),
    activeOccupancy: Number(response.activeOccupancy),
    organizer: resolveOrganizer(response.id, response.organizerId, response.organizer, labels),
    viewerRelation: response.viewerRelation,
  }
}

export function isKnownSessionStatus(status: string): status is SessionStatus {
  return (
    status === 'unconfirmed' ||
    status === 'confirmed' ||
    status === 'completed' ||
    status === 'expired' ||
    status === 'canceled'
  )
}
