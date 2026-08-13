import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

vi.mock('../../auth/useSession', () => ({
  useSession: () => ({ data: { userId: 'viewer-1' } }),
}))

import '../../i18n'
import { PollDetailPage } from './PollDetailPage'

function option(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'option-1',
    startTime: '2099-01-01T10:00:00Z',
    suggestedByUserId: null,
    suggestedByGuestIdentityId: null,
    availableCount: 3,
    maybeCount: 1,
    unavailableCount: 0,
    viewerVote: 'available',
    ...overrides,
  }
}

function pollDetail(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'poll-1',
    organizerId: 'organizer-1',
    title: 'Raid night poll',
    durationMinutes: 120,
    platform: 'notApplicable',
    game: null,
    notes: '',
    minAttendance: 4,
    maxCapacity: 4,
    isPrivate: false,
    deadlineChoice: 'hours24',
    deadline: '2099-01-02T00:00:00Z',
    selectionWindowEndsAt: '2099-01-04T00:00:00Z',
    status: 'polling',
    createdAt: '2099-01-01T00:00:00Z',
    selectedOptionId: null,
    createdSessionId: null,
    options: [
      option(),
      option({ id: 'option-2', startTime: '2020-01-01T10:00:00Z', viewerVote: null }),
    ],
    roster: [
      {
        participationId: 'participation-1',
        userId: 'organizer-1',
        guestIdentityId: null,
        displayName: 'Organizer One',
        isActive: true,
      },
    ],
    muted: false,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/polls/poll-1']}>
        <Routes>
          <Route path="/polls/:pollId" element={<PollDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockResolvedValue({ data: pollDetail() })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollAccessibilityTests', () => {
  /**
   * STYLING_OVERHAUL_DESIGN.md §3.2's S4 duty extension. Scoped to `role=radio` *button*
   * controls — VotingGrid's vote controls. The bare native radio inputs of the finalize
   * fieldset are exempt by name: their 44px duty is satisfied by the ≥44px wrapping-label
   * row plus `accent-color`, and their glyph does not grow.
   */
  it('every role=radio vote control meets the 44px touch target and visible-focus duties_D139', async () => {
    renderPage()
    await screen.findByTestId('vote-available-option-1')

    const voteControls = screen
      .getAllByRole('radio')
      .filter((control) => control.tagName === 'BUTTON')

    expect(voteControls).toHaveLength(6)
    for (const control of voteControls) {
      expect(control).toHaveClass('min-h-11')
      expect(control).toHaveClass('min-w-11')
      expect(control.className).toContain('focus-visible:outline-2')
      expect(control.className).toContain('focus-visible:outline-offset-2')
      expect(control.className).toContain('focus-visible:outline-focus')
    }
  })

  /**
   * R2's carve-out guard, from the other direction than the D-140 elapsed/future fact: the
   * option row's chrome must not encode the viewer's own vote either, so a voted row and an
   * unvoted one carry byte-identical class lists. The vote is restated in text instead.
   */
  it('an option row class list is identical whether or not the viewer has voted on it_D140', async () => {
    renderPage()

    const voted = await screen.findByTestId('poll-option-option-1')
    const unvoted = screen.getByTestId('poll-option-option-2')

    expect(unvoted.className).toBe(voted.className)
    expect(voted).toHaveTextContent('Your vote: Available')
    expect(unvoted).toHaveTextContent('You have not voted on this option yet.')
  })
})
