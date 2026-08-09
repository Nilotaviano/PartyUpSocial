import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../i18n'
import { PollDetailPage } from './PollDetailPage'

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
      {
        id: 'option-1',
        startTime: '2099-01-01T10:00:00Z',
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 3,
        maybeCount: 1,
        unavailableCount: 0,
        viewerVote: 'available',
      },
    ],
    roster: [
      {
        participationId: 'participation-1',
        userId: 'organizer-1',
        guestIdentityId: null,
        displayName: 'Organizer One',
        isActive: true,
      },
      {
        participationId: 'participation-2',
        userId: null,
        guestIdentityId: null,
        displayName: null,
        isActive: true,
      },
    ],
    ...overrides,
  }
}

function renderPage(pollId = 'poll-1') {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[`/polls/${pollId}`]}>
        <Routes>
          <Route path="/polls/:pollId" element={<PollDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollDetailPage', () => {
  it('renders option tallies as plain text and the viewer own vote statically_D140', async () => {
    api.get.mockResolvedValue({ data: pollDetail() })
    renderPage()

    expect(await screen.findByText('3 available · 1 maybe')).toBeInTheDocument()
    expect(screen.getByText('Your vote: Available')).toBeInTheDocument()
  })

  it('masks a roster entry with no display name as Anonymous User_D076', async () => {
    api.get.mockResolvedValue({ data: pollDetail() })
    renderPage()

    expect(await screen.findByText('Organizer One')).toBeInTheDocument()
    expect(screen.getByText('Anonymous User')).toBeInTheDocument()
  })

  it('presents closed-awaiting-selection from the clock gate, not materialized status alone', async () => {
    api.get.mockResolvedValue({
      data: pollDetail({
        status: 'polling',
        deadline: new Date(Date.now() - 60_000).toISOString(),
      }),
    })
    renderPage()

    expect(
      await screen.findByText('Voting has closed. The organizer is choosing a time.'),
    ).toBeInTheDocument()
  })

  it('links a finalized poll to its created session_D160', async () => {
    api.get.mockResolvedValue({
      data: pollDetail({ status: 'finalized', createdSessionId: 'session-9' }),
    })
    renderPage()

    expect(
      await screen.findByRole('link', { name: 'View the session' }),
    ).toHaveAttribute('href', '/sessions/session-9')
  })

  it('renders an expired terminal notice', async () => {
    api.get.mockResolvedValue({ data: pollDetail({ status: 'expired' }) })
    renderPage()

    expect(
      await screen.findByText('This poll expired without a selection.'),
    ).toBeInTheDocument()
  })

  it('renders a canceled terminal notice', async () => {
    api.get.mockResolvedValue({ data: pollDetail({ status: 'canceled' }) })
    renderPage()

    expect(await screen.findByText('This poll was canceled.')).toBeInTheDocument()
  })
})
