import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() }))
const session = vi.hoisted(() => ({ data: { userId: 'viewer-1' } }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

vi.mock('../../auth/useSession', () => ({
  useSession: () => session,
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
      {
        id: 'option-2',
        // D-140: an already-elapsed option, alongside the future one above.
        startTime: '2020-01-01T10:00:00Z',
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 2,
        maybeCount: 0,
        unavailableCount: 1,
        viewerVote: null,
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
    expect(await screen.findByTestId('vote-available-option-1')).toHaveAttribute(
      'aria-checked',
      'true',
    )
  })

  it('renders an elapsed option with the same ordinary styling as a future one, neither dimmed nor dropped_D140', async () => {
    api.get.mockResolvedValue({ data: pollDetail() })
    renderPage()

    const futureOption = await screen.findByTestId('poll-option-option-1')
    const pastOption = screen.getByTestId('poll-option-option-2')

    // Both remain present (D-140: a poll does not drop or expire elapsed
    // options), and share the exact same class list — no dimming/disabled
    // styling distinguishes the elapsed one.
    expect(pastOption).toBeInTheDocument()
    expect(pastOption.className).toBe(futureOption.className)
    expect(screen.getByText('2 available · 1 unavailable')).toBeInTheDocument()
    expect(screen.getByTestId('vote-available-option-2')).toHaveAttribute(
      'aria-checked',
      'false',
    )
    expect(screen.getByTestId('vote-maybe-option-2')).toHaveAttribute(
      'aria-checked',
      'false',
    )
    expect(screen.getByTestId('vote-unavailable-option-2')).toHaveAttribute(
      'aria-checked',
      'false',
    )
  })

  it('masks a roster entry with no display name as Anonymous User_D076', async () => {
    api.get.mockResolvedValue({ data: pollDetail() })
    renderPage()

    expect(await screen.findByText('Organizer One')).toBeInTheDocument()
    expect(screen.getByText('Anonymous User')).toBeInTheDocument()
  })

  describe('the clock-inclusive window gate (F1)', () => {
    it('pre-deadline: status polling, now < deadline -> ordinary polling chip and deadline text', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          status: 'polling',
          deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
          selectionWindowEndsAt: new Date(Date.now() + 48 * 60 * 60_000).toISOString(),
        }),
      })
      renderPage()

      expect(await screen.findByText(/^Voting closes /)).toBeInTheDocument()
      expect(
        screen.queryByText('Voting has closed. The organizer is choosing a time.'),
      ).not.toBeInTheDocument()
      expect(
        screen.queryByText("This poll's selection window has closed."),
      ).not.toBeInTheDocument()
    })

    it('in-window: deadline <= now < selectionWindowEndsAt (materialized closed) -> awaiting-selection', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          status: 'closed',
          deadline: new Date(Date.now() - 60_000).toISOString(),
          selectionWindowEndsAt: new Date(Date.now() + 47 * 60 * 60_000).toISOString(),
        }),
      })
      renderPage()

      expect(await screen.findByText('Closed · awaiting selection')).toBeInTheDocument()
      expect(
        screen.getByText('Voting has closed. The organizer is choosing a time.'),
      ).toBeInTheDocument()
    })

    it('past-window: now >= selectionWindowEndsAt, status still lagging polling -> expired-pending, not awaiting-selection', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          status: 'polling',
          deadline: new Date(Date.now() - 50 * 60 * 60_000).toISOString(),
          selectionWindowEndsAt: new Date(Date.now() - 60_000).toISOString(),
        }),
      })
      renderPage()

      expect(await screen.findByText('Closed · selection window ended')).toBeInTheDocument()
      expect(
        screen.getByText("This poll's selection window has closed."),
      ).toBeInTheDocument()
      expect(
        screen.queryByText('Voting has closed. The organizer is choosing a time.'),
      ).not.toBeInTheDocument()
    })
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

  describe('voting (S6)', () => {
    it('submits a per-option vote call, not a batch ballot_D172', async () => {
      api.get.mockResolvedValue({ data: pollDetail() })
      api.put.mockResolvedValue({ data: pollDetail() })
      renderPage()

      fireEvent.click(await screen.findByTestId('vote-maybe-option-2'))

      await waitFor(() =>
        expect(api.put).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/options/{optionId}/vote',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1', optionId: 'option-2' } }),
            body: { choice: 'maybe' },
          }),
        ),
      )
      expect(await screen.findByText('Your vote was saved.')).toBeInTheDocument()
    })

    it('revising an existing vote keeps focus on the clicked control_S6gate', async () => {
      api.get.mockResolvedValue({ data: pollDetail() })
      api.put.mockResolvedValue({ data: pollDetail() })
      renderPage()

      const maybeControl = await screen.findByTestId('vote-maybe-option-1')
      maybeControl.focus()
      fireEvent.click(maybeControl)

      await waitFor(() => expect(api.put).toHaveBeenCalled())
      await waitFor(() => expect(document.activeElement).toBe(maybeControl))
    })

    it('disables the grid once voting has closed', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          status: 'closed',
          deadline: new Date(Date.now() - 60_000).toISOString(),
        }),
      })
      renderPage()

      expect(await screen.findByTestId('vote-available-option-1')).toBeDisabled()
    })
  })

  describe('suggest option (S6, D-033/D-171)', () => {
    it('suggests an option and shows the D-080 merge-style confirmation', async () => {
      api.get.mockResolvedValue({ data: pollDetail() })
      api.post.mockResolvedValue({ data: pollDetail() })
      renderPage()

      fireEvent.change(await screen.findByLabelText('Suggest a time'), {
        target: { value: '2099-03-01T10:00' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/options',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
          }),
        ),
      )
      expect(await screen.findByText('Your suggestion was added.')).toBeInTheDocument()
    })

    it('renders the D-171 suggestion-cap copy, distinct from the D-131 creation-cap copy_D171', async () => {
      const ApiErrorClass = class extends Error {
        status = 422
        code = 'poll_option_cap'
      }
      api.get.mockResolvedValue({
        data: pollDetail({
          options: Array.from({ length: 12 }, (_, index) => ({
            id: `option-${index + 1}`,
            startTime: `2099-01-0${(index % 9) + 1}T10:00:00Z`,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 0,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
          })),
        }),
      })
      api.post.mockRejectedValue(new ApiErrorClass())
      renderPage()

      fireEvent.change(await screen.findByLabelText('Suggest a time'), {
        target: { value: '2099-03-01T10:00' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

      expect(
        await screen.findByText(
          'This poll already has 12 candidate times — the most it can hold.',
        ),
      ).toBeInTheDocument()
    })
  })

  describe('organizer guest panel (S6, D-044)', () => {
    it('mints a guest link, shows it plaintext-once, and lets the organizer revoke it', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({ organizerId: 'viewer-1' }),
      })
      api.post.mockResolvedValue({
        data: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
      })
      api.delete.mockResolvedValue({})
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Create link' }))

      expect(
        await screen.findByText('https://partyup.social/g/abc'),
      ).toBeInTheDocument()

      fireEvent.click(screen.getByRole('button', { name: 'Revoke' }))
      await waitFor(() =>
        expect(api.delete).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/guest-links/{guestLinkId}',
          expect.objectContaining({
            params: expect.objectContaining({
              path: { pollId: 'poll-1', guestLinkId: 'link-1' },
            }),
          }),
        ),
      )
      expect(screen.queryByText('https://partyup.social/g/abc')).not.toBeInTheDocument()
    })

    it('removes a claimed guest voter behind a confirmation dialog_D044', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          organizerId: 'viewer-1',
          roster: [
            {
              participationId: 'participation-3',
              userId: null,
              guestIdentityId: 'guest-9',
              displayName: 'Casey',
              isActive: true,
            },
          ],
        }),
      })
      api.post.mockResolvedValue({})
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Remove guest Casey' }))
      fireEvent.click(screen.getByRole('button', { name: 'Remove guest', hidden: true }))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/guests/{guestIdentityId}/remove',
          expect.objectContaining({
            params: expect.objectContaining({
              path: { pollId: 'poll-1', guestIdentityId: 'guest-9' },
            }),
          }),
        ),
      )
    })

    it('hides the guest panel from a non-organizer viewer', async () => {
      api.get.mockResolvedValue({ data: pollDetail({ organizerId: 'someone-else' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByText('Guest links')).not.toBeInTheDocument()
    })
  })
})
