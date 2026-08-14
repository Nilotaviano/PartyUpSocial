import { describe, expect, it } from 'vitest'
import {
  formatCountdown,
  mapFinalizationPreview,
  mapPollDetail,
  optionDayLabel,
  optionRange,
  rankedOptionIds,
  statusLabelKey,
  tallyFor,
  type PollDetailResponse,
  type PollFinalizationPreviewResponse,
  type PollFormatters,
  type PollLabels,
} from './pollData'

const labels: PollLabels = { anonymousVoter: 'Anonymous User' }

function formatters(): PollFormatters {
  return {
    day: new Intl.DateTimeFormat('en-US', { weekday: 'short', timeZone: 'UTC' }),
    time: new Intl.DateTimeFormat('en-US', {
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      timeZone: 'UTC',
    }),
  }
}

function pollResponse(overrides: Partial<PollDetailResponse> = {}): PollDetailResponse {
  return {
    id: 'poll-1',
    organizerId: 'user-organizer',
    title: 'Deep Rock co-op night',
    durationMinutes: 120,
    platform: 'PC · Discord',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    notes: 'Bring snacks.',
    minAttendance: 3,
    maxCapacity: 4,
    isPrivate: false,
    deadlineChoice: 'hours24',
    deadline: '2026-08-15T20:00:00Z',
    selectionWindowEndsAt: '2026-08-17T20:00:00Z',
    status: 'polling',
    createdAt: '2026-08-14T20:00:00Z',
    selectedOptionId: null,
    createdSessionId: null,
    options: [
      {
        id: 'opt-1',
        startTime: '2026-08-15T20:00:00Z',
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 2,
        maybeCount: 1,
        unavailableCount: 0,
        viewerVote: 'available',
        availableVoters: [
          {
            participationId: 'part-1',
            userId: 'user-organizer',
            guestIdentityId: null,
            displayName: 'Mika Ortega',
            isActive: true,
            avatar: { kind: 'emoji', emoji: '🐙', avatarSourceId: null, imgUrl: null },
          },
          {
            participationId: 'part-2',
            userId: 'user-2',
            guestIdentityId: null,
            displayName: null,
            isActive: true,
            avatar: null,
          },
        ],
        maybeVoters: [],
        unavailableVoters: [],
      },
      {
        id: 'opt-2',
        startTime: '2026-08-15T21:30:00Z',
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 3,
        maybeCount: 0,
        unavailableCount: 1,
        viewerVote: null,
        availableVoters: [],
        maybeVoters: [],
        unavailableVoters: [],
      },
    ],
    roster: [
      {
        participationId: 'part-1',
        userId: 'user-organizer',
        guestIdentityId: null,
        displayName: 'Mika Ortega',
        isActive: true,
        avatar: { kind: 'emoji', emoji: '🐙', avatarSourceId: null, imgUrl: null },
      },
      {
        participationId: 'part-2',
        userId: 'user-2',
        guestIdentityId: null,
        displayName: null,
        isActive: true,
        avatar: null,
      },
    ],
    muted: false,
    ...overrides,
  } as PollDetailResponse
}

describe('mapPollDetail', () => {
  it('returns null when the response is not yet loaded', () => {
    expect(mapPollDetail(undefined, 'UTC', labels)).toBeNull()
  })

  it('maps nullable maxCapacity through unchanged when present and when null', () => {
    const withCapacity = mapPollDetail(pollResponse(), 'UTC', labels)
    expect(withCapacity?.maxCapacity).toBe(4)

    const withoutCapacity = mapPollDetail(
      pollResponse({ maxCapacity: null }),
      'UTC',
      labels,
    )
    expect(withoutCapacity?.maxCapacity).toBeNull()
  })

  it('masks a roster entry with a null displayName and never leaks its avatar_D076', () => {
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)
    const masked = mapped?.roster.find((v) => v.id === 'part-2')
    expect(masked?.masked).toBe(true)
    expect(masked?.name).toBe('Anonymous User')
    expect(masked?.emoji).toBeNull()
    expect(masked?.imgUrl).toBeNull()

    const unmasked = mapped?.roster.find((v) => v.id === 'part-1')
    expect(unmasked?.masked).toBe(false)
    expect(unmasked?.name).toBe('Mika Ortega')
    expect(unmasked?.emoji).toBe('🐙')
  })

  it('converts isPrivate straight through and status to the wire PollStatus', () => {
    const mapped = mapPollDetail(pollResponse({ isPrivate: true }), 'UTC', labels)
    expect(mapped?.isPrivate).toBe(true)
    expect(mapped?.status).toBe('polling')
  })

  it('maps an option availableVoters list through the same identity resolution as the roster, masking a null displayName_D076', () => {
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)!
    const option = mapped.options.find((o) => o.id === 'opt-1')!
    expect(option.availableVoters).toHaveLength(2)

    const unmasked = option.availableVoters.find((v) => v.id === 'part-1')
    expect(unmasked?.masked).toBe(false)
    expect(unmasked?.name).toBe('Mika Ortega')
    expect(unmasked?.emoji).toBe('🐙')

    const masked = option.availableVoters.find((v) => v.id === 'part-2')
    expect(masked?.masked).toBe(true)
    expect(masked?.name).toBe('Anonymous User')
    expect(masked?.emoji).toBeNull()
    expect(masked?.imgUrl).toBeNull()
  })

  it('never re-derives a category count from its voter list length (A-076)', () => {
    // opt-2 carries a server availableCount of 3 but an empty availableVoters
    // list (defensive: the list and the count are allowed to disagree).
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)!
    const option = mapped.options.find((o) => o.id === 'opt-2')!
    expect(option.availableCount).toBe(3)
    expect(option.availableVoters).toEqual([])
  })
})

describe('D-052 ranking: Available then Maybe', () => {
  it('ranks the option with more Available votes first_D052', () => {
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)!
    expect(rankedOptionIds(mapped.options)).toEqual(['opt-2', 'opt-1'])
  })

  it('falls back to Maybe count when Available ties, then to earliest start_D052', () => {
    const mapped = mapPollDetail(
      pollResponse({
        options: [
          {
            id: 'a',
            startTime: '2026-08-15T22:00:00Z',
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 2,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          },
          {
            id: 'b',
            startTime: '2026-08-15T20:00:00Z',
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 2,
            maybeCount: 1,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }) as PollDetailResponse,
      'UTC',
      labels,
    )!
    expect(rankedOptionIds(mapped.options)).toEqual(['b', 'a'])
  })
})

describe('tallyFor', () => {
  it('reads the server-aggregated counts straight through', () => {
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)!
    const tally = tallyFor(mapped.options[0]!, 4)
    expect(tally).toEqual({ available: 2, maybe: 1, unavailable: 0, pending: 1 })
  })
})

describe('optionDayLabel/optionRange', () => {
  it('formats a fake-local option time as a sane HH:mm range', () => {
    const mapped = mapPollDetail(pollResponse(), 'UTC', labels)!
    const range = optionRange(mapped.options[0]!, mapped.durationMinutes, formatters())
    expect(range).toBe('20:00–22:00')
    expect(optionDayLabel(mapped.options[0]!, formatters())).toBe('Sat')
  })
})

describe('statusLabelKey', () => {
  it('maps every PollStatus to its own polls.json key', () => {
    expect(statusLabelKey('polling')).toBe('status.polling')
    expect(statusLabelKey('closed')).toBe('status.closed')
    expect(statusLabelKey('finalized')).toBe('status.finalized')
    expect(statusLabelKey('expired')).toBe('status.expired')
    expect(statusLabelKey('canceled')).toBe('status.canceled')
  })
})

describe('formatCountdown', () => {
  it('formats minutes as Hh MMm', () => {
    expect(formatCountdown(19 * 60 + 40)).toBe('19h 40m')
    expect(formatCountdown(5)).toBe('0h 05m')
  })

  it('floors an expired deadline at 0h 00m', () => {
    expect(formatCountdown(0)).toBe('0h 00m')
    expect(formatCountdown(-45)).toBe('0h 00m')
  })
})

describe('mapFinalizationPreview', () => {
  it('never re-derives seating locally — it passes the server allocation through per option', () => {
    const response: PollFinalizationPreviewResponse = {
      options: [
        {
          optionId: 'opt-1',
          startTime: '2026-08-15T20:00:00Z',
          meetsMinimum: true,
          roster: [
            {
              userId: 'user-organizer',
              guestIdentityId: null,
              displayName: 'Mika Ortega',
              status: 'going',
              isWaitlisted: false,
              avatar: null,
            },
            {
              userId: null,
              guestIdentityId: null,
              displayName: null,
              status: 'maybe',
              isWaitlisted: true,
              avatar: null,
            },
          ],
        },
      ],
    }

    const mapped = mapFinalizationPreview(response, labels)
    expect(mapped).toHaveLength(1)
    expect(mapped[0]?.meetsMinimum).toBe(true)
    expect(mapped[0]?.seats).toEqual([
      {
        voterId: 'user-organizer',
        name: 'Mika Ortega',
        seed: 'user-organizer',
        emoji: null,
        imgUrl: null,
        masked: false,
        status: 'going',
        seated: true,
      },
      {
        voterId: 'masked-opt-1-1',
        name: 'Anonymous User',
        seed: 'masked-opt-1-1',
        emoji: null,
        imgUrl: null,
        masked: true,
        status: 'maybe',
        seated: false,
      },
    ])
  })

  // D-076: PollContracts emits both `userId` and `guestIdentityId` as null on a
  // masked seat — the real wire shape, not a display name lookup keyed by a
  // synthetic user id like `user-2`. Two masked seats on the same option must
  // still get distinct voterIds (and thus distinct React keys / AvatarDisc
  // seeds), never both collapsing to the same fallback string.
  it('gives two masked seats on the same option distinct voterIds_D076', () => {
    const maskedEntry = {
      userId: null,
      guestIdentityId: null,
      displayName: null,
      status: 'pending',
      isWaitlisted: true,
      avatar: null,
    }
    const response: PollFinalizationPreviewResponse = {
      options: [
        {
          optionId: 'opt-1',
          startTime: '2026-08-15T20:00:00Z',
          meetsMinimum: false,
          roster: [maskedEntry, maskedEntry],
        },
      ],
    }

    const mapped = mapFinalizationPreview(response, labels)
    const voterIds = mapped[0]?.seats.map((seat) => seat.voterId) ?? []
    expect(voterIds).toHaveLength(2)
    expect(new Set(voterIds).size).toBe(2)
    expect(mapped[0]?.seats.every((seat) => seat.masked)).toBe(true)
  })

  it('returns an empty array when the response has not loaded', () => {
    expect(mapFinalizationPreview(undefined, labels)).toEqual([])
  })
})
