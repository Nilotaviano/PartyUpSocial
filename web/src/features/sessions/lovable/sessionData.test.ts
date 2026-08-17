import { describe, expect, it } from 'vitest'
import { isKnownSessionStatus, mapSessionSummary, type SessionSummaryResponse } from './sessionData'

const labels = { anonymousOrganizer: 'Anonymous User' }

function session(overrides: Partial<SessionSummaryResponse> = {}): SessionSummaryResponse {
  return {
    id: 'session-1',
    organizerId: 'user-organizer',
    title: 'Raid night',
    startTime: '2026-08-15T20:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    status: 'confirmed',
    visibility: 'public',
    viaGroup: null,
    sharedWithGroup: false,
    maxCapacity: 8,
    activeOccupancy: 2,
    organizer: { ordinal: 0, displayName: 'Mika Ortega', avatar: { kind: 'emoji', emoji: '🐙', avatarSourceId: null, imgUrl: null } },
    viewerRelation: 'organizer',
    ...overrides,
  } as SessionSummaryResponse
}

describe('mapSessionSummary', () => {
  it('maps nullable maxCapacity through unchanged when present and when null', () => {
    expect(mapSessionSummary(session(), 'UTC', labels).maxCapacity).toBe(8)
    expect(mapSessionSummary(session({ maxCapacity: null }), 'UTC', labels).maxCapacity).toBeNull()
  })

  it('converts visibility and status straight through the wire values', () => {
    const mapped = mapSessionSummary(session({ visibility: 'private', status: 'unconfirmed' }), 'UTC', labels)
    expect(mapped.visibility).toBe('private')
    expect(mapped.status).toBe('unconfirmed')
  })

  it('passes viaGroup and sharedWithGroup through unchanged_D206', () => {
    const mapped = mapSessionSummary(
      session({ visibility: 'groups', viaGroup: { id: 'g1', name: 'D&D crew' }, sharedWithGroup: true }),
      'UTC',
      labels,
    )
    expect(mapped.viaGroup).toEqual({ id: 'g1', name: 'D&D crew' })
    expect(mapped.sharedWithGroup).toBe(true)
  })

  it('maps a present game through unchanged and a null game through as null', () => {
    expect(mapSessionSummary(session(), 'UTC', labels).game).toEqual({
      name: 'Deep Rock Galactic',
      steamAppId: 548430,
    })
    expect(mapSessionSummary(session({ game: null }), 'UTC', labels).game).toBeNull()
  })

  it('resolves an unmasked organizer with the real name, emoji, and organizerId as its seed_D076', () => {
    const mapped = mapSessionSummary(session(), 'UTC', labels)
    expect(mapped.organizer).toEqual({
      seed: 'user-organizer',
      name: 'Mika Ortega',
      emoji: '🐙',
      imgUrl: null,
      masked: false,
    })
  })

  it('masks an organizer with a null displayName and never leaks its avatar_D076', () => {
    const mapped = mapSessionSummary(
      session({
        organizerId: null,
        organizer: { ordinal: 0, displayName: null, avatar: { kind: 'emoji', emoji: '🐙', avatarSourceId: null, imgUrl: null } },
      }),
      'UTC',
      labels,
    )
    expect(mapped.organizer.masked).toBe(true)
    expect(mapped.organizer.name).toBe('Anonymous User')
    expect(mapped.organizer.emoji).toBeNull()
    expect(mapped.organizer.imgUrl).toBeNull()
  })

  it('gives two masked organizers on different sessions distinct seeds_D076', () => {
    const maskedOrganizer = { ordinal: 0, displayName: null, avatar: null }
    const a = mapSessionSummary(
      session({ id: 'session-a', organizerId: null, organizer: maskedOrganizer }),
      'UTC',
      labels,
    )
    const b = mapSessionSummary(
      session({ id: 'session-b', organizerId: null, organizer: maskedOrganizer }),
      'UTC',
      labels,
    )
    expect(a.organizer.seed).not.toBe(b.organizer.seed)
  })

  it('passes viewerRelation through unchanged', () => {
    expect(mapSessionSummary(session({ viewerRelation: 'invitee' }), 'UTC', labels).viewerRelation).toBe(
      'invitee',
    )
  })

  it('D-046: converts the wire UTC startTime into a fake-local date carrying the target zone wall clock', () => {
    const mapped = mapSessionSummary(session({ startTime: '2026-08-15T23:00:00Z' }), 'Pacific/Honolulu', labels)
    // Honolulu is UTC-10: 23:00Z reads back as 13:00 wall clock via the UTC accessors.
    expect(mapped.startTime.getUTCHours()).toBe(13)
  })
})

describe('isKnownSessionStatus', () => {
  it('accepts every SessionStatusChip-vocabulary status', () => {
    for (const status of ['unconfirmed', 'confirmed', 'completed', 'expired', 'canceled']) {
      expect(isKnownSessionStatus(status)).toBe(true)
    }
  })

  it('rejects a status this build does not understand', () => {
    expect(isKnownSessionStatus('archived')).toBe(false)
  })
})
