import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../i18n'
import { VotingGrid } from './VotingGrid'

function options() {
  return [
    {
      id: 'option-1',
      startTime: '2099-01-01T10:00:00Z',
      suggestedByUserId: null,
      suggestedByGuestIdentityId: null,
      availableCount: 0,
      maybeCount: 0,
      unavailableCount: 0,
      availableVoters: [],
      maybeVoters: [],
      unavailableVoters: [],
      viewerVote: null,
    },
  ]
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('VotingGrid', () => {
  it('renders each option row as an independent radiogroup, not a composite grid', () => {
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={options()}
        pendingOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('vote-radiogroup-option-1')).toHaveAttribute(
      'role',
      'radiogroup',
    )
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
  })

  it('renders the value glyph aria-hidden with the accessible name carrying "{time}, {value}"', () => {
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={options()}
        pendingOptionId={null}
        onVote={vi.fn()}
      />,
    )

    const available = screen.getByTestId('vote-available-option-1')
    expect(available).toHaveAccessibleName(/, Available$/)
    expect(available.querySelector('[aria-hidden="true"]')).toHaveTextContent('✓')
  })

  it('only the checked radio is in the tab order; the rest carry tabindex -1_APG', () => {
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={[{ ...options()[0], viewerVote: 'maybe' }]}
        pendingOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('vote-available-option-1')).toHaveAttribute('tabindex', '-1')
    expect(screen.getByTestId('vote-maybe-option-1')).toHaveAttribute('tabindex', '0')
    expect(screen.getByTestId('vote-unavailable-option-1')).toHaveAttribute('tabindex', '-1')
  })

  it('ArrowRight moves selection to the next choice and focus follows it_APG', () => {
    const onVote = vi.fn()
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={[{ ...options()[0], viewerVote: 'available' }]}
        pendingOptionId={null}
        onVote={onVote}
      />,
    )

    screen.getByTestId('vote-available-option-1').focus()
    fireEvent.keyDown(screen.getByTestId('vote-available-option-1'), { key: 'ArrowRight' })

    expect(onVote).toHaveBeenCalledWith('option-1', 'maybe')
  })

  it('ArrowLeft wraps from the first choice to the last_APG', () => {
    const onVote = vi.fn()
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={[{ ...options()[0], viewerVote: 'available' }]}
        pendingOptionId={null}
        onVote={onVote}
      />,
    )

    fireEvent.keyDown(screen.getByTestId('vote-available-option-1'), { key: 'ArrowLeft' })

    expect(onVote).toHaveBeenCalledWith('option-1', 'unavailable')
  })

  it('disables every control when the grid is disabled (closed presentation)', () => {
    render(
      <VotingGrid
        disabled
        locale="en-US"
        options={options()}
        pendingOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('vote-available-option-1')).toBeDisabled()
    expect(screen.getByTestId('vote-maybe-option-1')).toBeDisabled()
    expect(screen.getByTestId('vote-unavailable-option-1')).toBeDisabled()
  })

  it('renders an elapsed option ordinarily — the tally reads plain text, no dimming_D140', () => {
    render(
      <VotingGrid
        disabled={false}
        locale="en-US"
        options={[
          { ...options()[0], startTime: '2020-01-01T10:00:00Z', availableCount: 2 },
        ]}
        pendingOptionId={null}
        onVote={vi.fn()}
      />,
    )

    expect(screen.getByTestId('poll-option-tally-option-1')).toHaveTextContent('2 available')
  })
})
