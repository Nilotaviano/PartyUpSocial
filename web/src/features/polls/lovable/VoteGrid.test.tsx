import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../../i18n'
import { VoteGrid } from './VoteGrid'
import type { PollFormatters, PollOptionT } from './pollData'

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

const formatters: PollFormatters = {
  day: new Intl.DateTimeFormat('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  }),
  time: new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: '2-digit', timeZone: 'UTC' }),
}

function option(overrides: Partial<PollOptionT> = {}): PollOptionT {
  return {
    id: 'option-1',
    start: new Date('2099-01-01T10:00:00Z'),
    durationMinutes: 60,
    availableCount: 0,
    maybeCount: 0,
    unavailableCount: 0,
    viewerVote: null,
    availableVoters: [],
    maybeVoters: [],
    unavailableVoters: [],
    suggestedBy: null,
    ...overrides,
  }
}

describe('VoteGrid', () => {
  it('renders nothing when there are no options', () => {
    const { container } = render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )
    expect(container).toBeEmptyDOMElement()
  })

  it("paints each option's own band at its own D-211 duration_D211", () => {
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[
          option({ id: 'option-1', durationMinutes: 90 }),
          option({ id: 'option-2', durationMinutes: 240 }),
        ]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    const end = (iso: string) =>
      new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: '2-digit', timeZone: 'UTC' }).format(
        new Date(iso),
      )
    expect(screen.getByTestId('vote-grid-band-option-1').textContent).toContain(
      end('2099-01-01T11:30:00Z'),
    )
    expect(screen.getByTestId('vote-grid-band-option-2').textContent).toContain(
      end('2099-01-01T14:00:00Z'),
    )
  })

  it('tapping a band with no vote yet casts Available, the first step of the cycle_D211', () => {
    const onVote = vi.fn()
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: null })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={onVote}
      />,
    )

    fireEvent.click(screen.getByTestId('vote-grid-band-option-1'))
    expect(onVote).toHaveBeenCalledWith('option-1', 'available')
  })

  it('tapping cycles Available -> Maybe -> Unavailable -> Available, reusing the same vote mutation_D211', () => {
    const onVote = vi.fn()
    const { rerender } = render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: 'available' })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={onVote}
      />,
    )
    fireEvent.click(screen.getByTestId('vote-grid-band-option-1'))
    expect(onVote).toHaveBeenLastCalledWith('option-1', 'maybe')

    rerender(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: 'maybe' })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={onVote}
      />,
    )
    fireEvent.click(screen.getByTestId('vote-grid-band-option-1'))
    expect(onVote).toHaveBeenLastCalledWith('option-1', 'unavailable')

    rerender(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: 'unavailable' })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={onVote}
      />,
    )
    fireEvent.click(screen.getByTestId('vote-grid-band-option-1'))
    expect(onVote).toHaveBeenLastCalledWith('option-1', 'available')
  })

  it('carries an accessible name with the option time and the current vote', () => {
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: 'maybe' })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('vote-grid-band-option-1')).toHaveAccessibleName(/Maybe/)
  })

  it('a band with no vote yet names itself accordingly', () => {
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option({ viewerVote: null })]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('vote-grid-band-option-1')).toHaveAccessibleName(
      /have not voted on this option yet/,
    )
  })

  it('carries real button semantics, disabled when voting is closed', () => {
    render(
      <VoteGrid
        disabled
        formatters={formatters}
        options={[option()]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    const band = screen.getByTestId('vote-grid-band-option-1')
    expect(band.tagName).toBe('BUTTON')
    expect(band).toBeDisabled()
  })

  it('an in-flight vote on this band never disables it (focus must survive the mutation)', () => {
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[option()]}
        pendingOptionId="option-1"
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    const band = screen.getByTestId('vote-grid-band-option-1')
    expect(band).not.toBeDisabled()
    expect(band).toHaveAttribute('aria-busy', 'true')
  })

  it('groups options landing on the same real day into one column, others into their own', () => {
    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={[
          option({ id: 'option-1', start: new Date('2099-01-01T10:00:00Z') }),
          option({ id: 'option-2', start: new Date('2099-01-01T18:00:00Z') }),
          option({ id: 'option-3', start: new Date('2099-01-02T10:00:00Z') }),
        ]}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getAllByTestId(/^vote-grid-day-/)).toHaveLength(2)
  })

  it('pages by 3 days on a narrow viewport instead of overflowing horizontally', () => {
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation((query: string) => ({
        matches: true,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    )
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 375 })

    render(
      <VoteGrid
        disabled={false}
        formatters={formatters}
        options={Array.from({ length: 5 }, (_, i) =>
          option({
            id: `option-${i}`,
            start: new Date(new Date('2099-01-01T10:00:00Z').getTime() + i * 24 * 60 * 60_000),
          }),
        )}
        pendingOptionId={null}
        selectedOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getAllByTestId(/^vote-grid-day-/)).toHaveLength(3)
    expect(screen.getByTestId('vote-grid-pager')).toBeInTheDocument()

    vi.unstubAllGlobals()
  })
})
