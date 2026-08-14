import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CandidateGrid, type Candidate } from './CandidateGrid'
import { candidateKey } from './pollData'
import type { Friend, WeekLabels } from '../../availability/lovable/partyup-data'

afterEach(() => {
  cleanup()
})

const labels: WeekLabels = {
  dayNames: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'],
  dayLongNames: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'],
  dayDates: ['10 Aug', '11 Aug', '12 Aug', '13 Aug', '14 Aug', '15 Aug', '16 Aug'],
}

const freeCountLabel = (day: string, time: string, count: number) =>
  `${day} ${time} — ${count} friends free`

function baseProps(overrides: Partial<Parameters<typeof CandidateGrid>[0]> = {}) {
  const onToggle = vi.fn()
  const props = {
    days: [0],
    rangeStart: 20 * 60,
    rangeEnd: 21 * 60,
    step: 30,
    durationMin: 30,
    candidates: [] as Candidate[],
    onToggle,
    max: 12,
    friends: [] as Friend[],
    labels,
    freeCountLabel,
    ...overrides,
  }
  return { onToggle, props }
}

describe('candidateKey', () => {
  it('is stable and unique per day/start pair', () => {
    expect(candidateKey({ day: 1, start: 90 })).toBe('1-90')
    expect(candidateKey({ day: 1, start: 90 })).not.toBe(candidateKey({ day: 2, start: 90 }))
  })
})

describe('CandidateGrid max cap', () => {
  it('disables an unselected slot once candidates.length reaches max', () => {
    const { props } = baseProps({
      candidates: [{ day: 0, start: 20 * 60 }],
      max: 1,
    })
    render(<CandidateGrid {...props} />)

    const untouched = screen.getByRole('button', { name: /20:30/ })
    expect(untouched).toBeDisabled()
  })

  it('keeps an already-selected slot enabled (togglable) even when full', () => {
    const { props } = baseProps({
      candidates: [{ day: 0, start: 20 * 60 }],
      max: 1,
    })
    render(<CandidateGrid {...props} />)

    const selected = screen.getByRole('button', { name: /^Mon 20:00/ })
    expect(selected).not.toBeDisabled()
    expect(selected).toHaveAttribute('aria-pressed', 'true')
  })

  it('does not disable any slot below the cap', () => {
    const { props } = baseProps({ candidates: [], max: 12 })
    render(<CandidateGrid {...props} />)

    for (const button of screen.getAllByRole('button')) {
      expect(button).not.toBeDisabled()
    }
  })
})

describe('CandidateGrid friend availability chips', () => {
  it('shows a free-friend disc for a slot inside a friend window', () => {
    const friends: Friend[] = [
      {
        id: 'friend-1',
        name: 'Mika',
        emoji: '🐙',
        imgUrl: null,
        masked: false,
        windows: [{ day: 0, start: 20 * 60, end: 20 * 60 + 30, intent: 'free' }],
      },
    ]
    const { props } = baseProps({ friends })
    render(<CandidateGrid {...props} />)

    expect(
      screen.getByRole('button', { name: 'Mon 20:00 — 1 friends free' }),
    ).toBeInTheDocument()
  })

  it('excludes a maybe window from the free-friend count_F4', () => {
    const friends: Friend[] = [
      {
        id: 'friend-1',
        name: 'Mika',
        emoji: '🐙',
        imgUrl: null,
        masked: false,
        windows: [{ day: 0, start: 20 * 60, end: 20 * 60 + 30, intent: 'maybe' }],
      },
      {
        id: 'friend-2',
        name: 'Sam',
        emoji: '🐧',
        imgUrl: null,
        masked: false,
        windows: [{ day: 0, start: 20 * 60, end: 20 * 60 + 30, intent: 'free' }],
      },
    ]
    const { props } = baseProps({ friends })
    render(<CandidateGrid {...props} />)

    // Only the `free` friend counts — the `maybe` friend never inflates the figure.
    expect(
      screen.getByRole('button', { name: 'Mon 20:00 — 1 friends free' }),
    ).toBeInTheDocument()
  })
})
