import { describe, expect, it } from 'vitest'
import type { CalendarView } from '../availabilityQueries'
import { mapCalendar } from './calendarData'

const labels = {
  anonymousFriend: 'Anonymous User',
  voteLabel: (vote: string | null) => vote ?? 'no vote',
}

/** The Monday of the fixture week, in the fake-local (UTC accessors) frame the grid uses. */
const weekStart = new Date(Date.UTC(2026, 7, 10, 0, 0, 0, 0))

function viewWithPollOptions(
  options: CalendarView['pollOptions'][number]['options'],
): CalendarView {
  return {
    myAvailability: { occurrences: [], series: [], merged: [], range: null },
    friendAvailability: [],
    sessions: [],
    pollOptions: [
      {
        pollId: '44444444-4444-4444-4444-444444444444',
        title: 'Deep Rock co-op',
        options,
      },
    ],
  } as CalendarView
}

describe('mapCalendar poll layer', () => {
  it('paints each candidate pill across its own D-211 duration_D211', () => {
    const mapped = mapCalendar(
      viewWithPollOptions([
        {
          optionId: '55555555-5555-5555-5555-555555555555',
          startTime: '2026-08-13T14:00:00Z',
          durationMinutes: 15,
          ownVote: 'available',
        },
        {
          optionId: '66666666-6666-6666-6666-666666666666',
          startTime: '2026-08-13T14:00:00Z',
          durationMinutes: 300,
          ownVote: null,
        },
      ]),
      weekStart,
      'UTC',
      labels,
    )

    const [short, long] = mapped.pollGroups[0].options
    // Both start at 14:00 on Thursday (day index 3 of a Monday-start week).
    expect(short).toMatchObject({ day: 3, start: 14 * 60, end: 14 * 60 + 15 })
    expect(long).toMatchObject({ day: 3, start: 14 * 60, end: 14 * 60 + 300 })
    expect(short.end).not.toBe(long.end)
  })

  it('does not fall back to a fixed one-hour block for a long option_D211', () => {
    const mapped = mapCalendar(
      viewWithPollOptions([
        {
          optionId: '77777777-7777-7777-7777-777777777777',
          startTime: '2026-08-11T18:00:00Z',
          durationMinutes: 180,
          ownVote: null,
        },
      ]),
      weekStart,
      'UTC',
      labels,
    )

    const [option] = mapped.polls
    expect(option.end - option.start).toBe(180)
  })

  it('clips a candidate that runs past midnight at the day boundary_D211', () => {
    const mapped = mapCalendar(
      viewWithPollOptions([
        {
          optionId: '88888888-8888-8888-8888-888888888888',
          startTime: '2026-08-11T23:00:00Z',
          durationMinutes: 240,
          ownVote: null,
        },
      ]),
      weekStart,
      'UTC',
      labels,
    )

    // The grid paints one band per option; the first day's segment is the one shown.
    const [option] = mapped.polls
    expect(option).toMatchObject({ day: 1, start: 23 * 60, end: 24 * 60 })
  })
})
