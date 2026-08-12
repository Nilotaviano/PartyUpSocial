import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
    muted: false,
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
          {/* D-160: a real route the finalize navigation must actually land on — not just a
              same-origin POST call — the honest way to prove `navigate` fired. */}
          <Route
            path="/sessions/:sessionId"
            element={<div data-testid="navigated-to-session" />}
          />
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

  it('mutes an unmuted, non-terminal poll via PUT_D090', async () => {
    api.get.mockResolvedValue({ data: pollDetail({ status: 'polling' }) })
    api.put.mockResolvedValue({ data: {} })
    renderPage()

    const toggle = await screen.findByRole('button', { name: 'Mute' })
    expect(toggle).not.toBeDisabled()
    fireEvent.click(toggle)

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/polls/{pollId}/mute',
        expect.objectContaining({
          params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
        }),
      ),
    )
  })

  it('unmutes a muted poll via DELETE_D090', async () => {
    api.get.mockResolvedValue({ data: pollDetail({ status: 'polling', muted: true }) })
    api.delete.mockResolvedValue({})
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Unmute' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/polls/{pollId}/mute',
        expect.objectContaining({
          params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
        }),
      ),
    )
  })

  it.each(['finalized', 'expired', 'canceled'])(
    'disables the mute toggle on a terminal (%s) poll_D113',
    async (status) => {
      api.get.mockResolvedValue({ data: pollDetail({ status }) })
      renderPage()

      expect(await screen.findByRole('button', { name: 'Mute' })).toBeDisabled()
    },
  )

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

    it('a rejected vote on one option leaves an already-accepted vote on another standing_F4a_design7note1', async () => {
      let optionOneVote: string | null = null
      api.get.mockImplementation(() =>
        Promise.resolve({
          data: pollDetail({
            options: [
              {
                id: 'option-1',
                startTime: '2099-01-01T10:00:00Z',
                suggestedByUserId: null,
                suggestedByGuestIdentityId: null,
                availableCount: 0,
                maybeCount: 0,
                unavailableCount: 0,
                viewerVote: optionOneVote,
              },
              {
                id: 'option-2',
                startTime: '2099-01-02T10:00:00Z',
                suggestedByUserId: null,
                suggestedByGuestIdentityId: null,
                availableCount: 0,
                maybeCount: 0,
                unavailableCount: 0,
                viewerVote: null,
              },
            ],
          }),
        }),
      )
      const RejectedError = class extends Error {
        status = 422
        code = 'poll_voting_closed'
      }
      api.put.mockImplementation(
        (_path: string, options: { params: { path: { optionId: string } } }) => {
          if (options.params.path.optionId === 'option-2') {
            return Promise.reject(new RejectedError())
          }
          optionOneVote = 'maybe'
          return Promise.resolve({ data: pollDetail() })
        },
      )
      renderPage()

      fireEvent.click(await screen.findByTestId('vote-maybe-option-1'))
      await waitFor(() =>
        expect(screen.getByTestId('vote-maybe-option-1')).toHaveAttribute('aria-checked', 'true'),
      )

      fireEvent.click(screen.getByTestId('vote-available-option-2'))
      await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())

      // A "submit all" affordance never exists here — vote B's rejection leaves vote A's
      // already-accepted state untouched (per-option submission, design §7 note 1).
      expect(screen.getByTestId('vote-maybe-option-1')).toHaveAttribute('aria-checked', 'true')
      expect(screen.getByTestId('vote-available-option-2')).toHaveAttribute(
        'aria-checked',
        'false',
      )
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

    it('renders the D-044 remove-guest-instead copy on a poll_conflict revoke, and the link stays listed_F1', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({ organizerId: 'viewer-1' }),
      })
      api.post.mockResolvedValue({
        data: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
      })
      const ConflictError = class extends Error {
        status = 409
        code = 'poll_conflict'
      }
      api.delete.mockRejectedValue(new ConflictError())
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Create link' }))
      expect(
        await screen.findByText('https://partyup.social/g/abc'),
      ).toBeInTheDocument()

      fireEvent.click(screen.getByRole('button', { name: 'Revoke' }))

      expect(
        await screen.findByText(
          "This guest has already claimed their link and can't be revoked. Remove the guest instead.",
        ),
      ).toBeInTheDocument()
      // The failed revoke never removed the link from local state.
      expect(screen.getByText('https://partyup.social/g/abc')).toBeInTheDocument()
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

  describe('selection/finalize (S7, design §5/§6.1)', () => {
    function awaitingSelectionPoll(overrides: Partial<Record<string, unknown>> = {}) {
      return pollDetail({
        organizerId: 'viewer-1',
        status: 'closed',
        deadline: new Date(Date.now() - 60_000).toISOString(),
        selectionWindowEndsAt: new Date(Date.now() + 47 * 60 * 60_000).toISOString(),
        ...overrides,
      })
    }

    function previewResponse() {
      return {
        options: [
          {
            optionId: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            meetsMinimum: true,
            roster: [
              {
                userId: 'organizer-1',
                guestIdentityId: null,
                displayName: 'Organizer One',
                status: 'going',
                isWaitlisted: false,
              },
              {
                userId: 'voter-2',
                guestIdentityId: null,
                displayName: 'Voter Two',
                status: 'going',
                isWaitlisted: true,
              },
            ],
          },
        ],
      }
    }

    it('fetches the finalization preview and renders the exact preview-guarantee copy, never a stronger claim', async () => {
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview') ? previewResponse() : awaitingSelectionPoll(),
        }),
      )
      renderPage()

      expect(
        await screen.findByText(
          "This preview and finalizing use the same rule — the same inputs always produce the same result. If something changes in between (an option is removed, or someone's access changes), the outcome may differ from what's shown here.",
        ),
      ).toBeInTheDocument()
      expect(await screen.findByTestId('finalize-option-option-1')).toBeInTheDocument()
      expect(screen.getByText('Going: Organizer One')).toBeInTheDocument()
      expect(screen.getByText('Waitlisted: Voter Two')).toBeInTheDocument()
    })

    it('finalizes the selected option and navigates to the created session_D160', async () => {
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview') ? previewResponse() : awaitingSelectionPoll(),
        }),
      )
      api.post.mockResolvedValue({
        data: { sessionId: 'session-42', poll: awaitingSelectionPoll({ status: 'finalized' }) },
      })
      renderPage()

      const finalizeOption = await screen.findByTestId('finalize-option-option-1')
      fireEvent.click(within(finalizeOption).getByRole('radio'))
      fireEvent.click(await screen.findByTestId('finalize-trigger'))
      fireEvent.click(screen.getByTestId('finalize-confirm'))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/finalize',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
            body: { optionId: 'option-1' },
          }),
        ),
      )
      expect(await screen.findByTestId('navigated-to-session')).toBeInTheDocument()
    })

    it('renders the honest 409/422 taxonomy on a premature preview fetch, never fabricating options', async () => {
      const PrematureError = class extends Error {
        status = 422
        code = 'poll_invalid_transition'
      }
      api.get.mockImplementation((path: string) =>
        path.includes('finalization-preview')
          ? Promise.reject(new PrematureError())
          : Promise.resolve({ data: awaitingSelectionPoll() }),
      )
      renderPage()

      expect(await screen.findByText('That change is no longer possible.')).toBeInTheDocument()
      expect(screen.queryByTestId('finalize-option-option-1')).not.toBeInTheDocument()
    })

    /**
     * D-187: a still-polling poll offers the organizer an early finalize, but only for options the
     * server reports as viable, and the copy has to say that confirming ends voting now.
     */
    it('offers early finalization on a viable option while the poll is still polling_D187', async () => {
      const preview = previewResponse()
      preview.options.push({
        optionId: 'option-2',
        startTime: '2099-02-01T10:00:00Z',
        meetsMinimum: false,
        roster: [],
      })
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? preview
            : pollDetail({
                organizerId: 'viewer-1',
                status: 'polling',
                deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
              }),
        }),
      )
      renderPage()

      expect(await screen.findByText('Finalize early')).toBeInTheDocument()
      expect(
        screen.getByText(
          'Enough people are available for at least one time. Finalizing now ends voting immediately and creates the session.',
        ),
      ).toBeInTheDocument()
      expect(screen.getByTestId('finalize-option-option-1')).toBeInTheDocument()
      // The non-viable option is not selectable early at all — it is not offered.
      expect(screen.queryByTestId('finalize-option-option-2')).not.toBeInTheDocument()

      fireEvent.click(
        within(screen.getByTestId('finalize-option-option-1')).getByRole('radio'),
      )
      fireEvent.click(await screen.findByTestId('finalize-trigger'))

      expect(screen.getByText('End voting and finalize?')).toBeInTheDocument()
      expect(
        screen.getByText(/This closes voting right now and creates a session at/),
      ).toBeInTheDocument()
    })

    it('offers no early finalization while no option is viable_D187', async () => {
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? {
                options: [
                  {
                    optionId: 'option-1',
                    startTime: '2099-01-01T10:00:00Z',
                    meetsMinimum: false,
                    roster: [],
                  },
                ],
              }
            : pollDetail({
                organizerId: 'viewer-1',
                status: 'polling',
                deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
              }),
        }),
      )
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByText('Finalize early')).not.toBeInTheDocument()
      expect(screen.queryByTestId('finalize-option-option-1')).not.toBeInTheDocument()
    })

    it('shows no selection controls in expiredPending, only the S5 notice', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          organizerId: 'viewer-1',
          status: 'polling',
          deadline: new Date(Date.now() - 50 * 60 * 60_000).toISOString(),
          selectionWindowEndsAt: new Date(Date.now() - 60_000).toISOString(),
        }),
      })
      renderPage()

      expect(
        await screen.findByText("This poll's selection window has closed."),
      ).toBeInTheDocument()
      expect(screen.queryByText('Choose a time')).not.toBeInTheDocument()
    })

    it('renders a disc per finalization preview roster row without rewriting the radio accessible name_D188', async () => {
      const preview = previewResponse()
      const previewWithAvatars = {
        options: preview.options.map((option) => ({
          ...option,
          roster: option.roster.map((row) => ({
            ...row,
            avatar: { kind: 'emoji', emoji: '🦄', avatarSourceId: null, imgUrl: null },
          })),
        })),
      }
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? previewWithAvatars
            : awaitingSelectionPoll(),
        }),
      )
      renderPage()

      const option = await screen.findByTestId('finalize-option-option-1')
      // Discs render (one per going + one per waitlisted row), but each lives in its
      // own aria-hidden span, so the radio's accessible name still comes only from
      // the comma-joined name text — the emoji never enters it.
      const discs = option.querySelectorAll('[aria-hidden="true"]')
      expect(discs.length).toBeGreaterThan(0)
      discs.forEach((disc) => expect(disc).toHaveTextContent('🦄'))
      const radio = within(option).getByRole('radio')
      expect(radio).not.toHaveAccessibleName(/🦄/)
      expect(radio).toHaveAccessibleName(/Organizer One/)
      expect(radio).toHaveAccessibleName(/Voter Two/)
    })
  })

  describe('organizer guest panel continued (S6, D-044)', () => {
    it('hides the mint control in awaitingSelection_F4b', async () => {
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? { options: [] }
            : pollDetail({
                organizerId: 'viewer-1',
                status: 'closed',
                deadline: new Date(Date.now() - 60_000).toISOString(),
              }),
        }),
      )
      renderPage()

      expect(await screen.findByText('Guest links')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Create link' })).not.toBeInTheDocument()
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
    })

    it('a link minted before the deadline stays listed and revokable once the poll moves to awaitingSelection_F4c', async () => {
      let closed = false
      api.get.mockImplementation((path: string) =>
        path.includes('finalization-preview')
          ? Promise.resolve({ data: { options: [] } })
          : Promise.resolve({
              data: pollDetail({
                organizerId: 'viewer-1',
                status: closed ? 'closed' : 'polling',
                deadline: closed
                  ? new Date(Date.now() - 60_000).toISOString()
                  : new Date(Date.now() + 60 * 60_000).toISOString(),
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
            }),
      )
      api.post.mockImplementation((path: string) =>
        path === '/api/v1/polls/{pollId}/guest-links'
          ? Promise.resolve({
              data: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
            })
          : Promise.resolve({}),
      )
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Create link' }))
      expect(
        await screen.findByText('https://partyup.social/g/abc'),
      ).toBeInTheDocument()

      // Removing the claimed guest (D-044) also flips the mocked poll to closed/past-deadline
      // (awaitingSelection) and invalidates the query — an in-place transition, same component.
      closed = true
      fireEvent.click(screen.getByRole('button', { name: 'Remove guest Casey' }))
      fireEvent.click(screen.getByRole('button', { name: 'Remove guest', hidden: true }))

      expect(
        await screen.findByText('Closed · awaiting selection'),
      ).toBeInTheDocument()
      // Minting is gone now, but the link minted before the transition stays listed and
      // revokable — revoke rendering is never gated by `canMint`.
      expect(screen.queryByRole('button', { name: 'Create link' })).not.toBeInTheDocument()
      expect(screen.getByText('https://partyup.social/g/abc')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Revoke' })).toBeInTheDocument()
    })
  })

  describe('organizer mid-flight invite link (D-193)', () => {
    it('shows the invite link for the organizer while Polling_D193', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({ organizerId: 'viewer-1', status: 'polling' }),
      })
      renderPage()

      expect(await screen.findByRole('link', { name: 'Invite' })).toHaveAttribute(
        'href',
        '/polls/poll-1/invite',
      )
    })

    // Security-shaped, mirroring the D-156 cancel-control pair above: proves the link is
    // never rendered for a viewer who is not the organizer, catching a regression where the
    // `isOrganizer` gate is dropped or inverted even while the organizer-visible test passes.
    it('never shows the invite link to a non-organizer_D193', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({ organizerId: 'someone-else', status: 'polling' }),
      })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('link', { name: 'Invite' })).not.toBeInTheDocument()
    })

    it('hides the invite link once the poll has left Polling (Closed)_D193', async () => {
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? { options: [] }
            : pollDetail({
                organizerId: 'viewer-1',
                status: 'closed',
                deadline: new Date(Date.now() - 60_000).toISOString(),
              }),
        }),
      )
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('link', { name: 'Invite' })).not.toBeInTheDocument()
    })
  })

  describe('organizer cancel (D-156)', () => {
    it('an organizer confirms cancel, POSTs to the endpoint and lands on /polls_D156', async () => {
      api.get.mockResolvedValue({ data: pollDetail({ organizerId: 'viewer-1' }) })
      api.post.mockResolvedValue({})
      render(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <MemoryRouter initialEntries={['/polls/poll-1']}>
            <Routes>
              <Route path="/polls/:pollId" element={<PollDetailPage />} />
              <Route path="/polls" element={<div data-testid="navigated-to-polls" />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>,
      )

      fireEvent.click(await screen.findByRole('button', { name: 'Cancel poll' }))
      const dialog = screen.getByRole('dialog')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel poll' }))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/cancel',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
          }),
        ),
      )
      expect(await screen.findByTestId('navigated-to-polls')).toBeInTheDocument()
    })

    // Security-shaped: proves the control is never rendered for a viewer who is not the
    // organizer — pre-change code has no cancel control at all, so this would already pass
    // vacuously without the trigger-presence assertion in the organizer test above pairing
    // it. This test alone catches a regression where the gate is dropped (e.g. `isOrganizer`
    // check removed or inverted) while the organizer-visible test still passes.
    it('never shows the cancel control to a non-organizer_D156', async () => {
      api.get.mockResolvedValue({ data: pollDetail({ organizerId: 'someone-else' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Cancel poll' })).not.toBeInTheDocument()
    })

    it('hides the cancel control once the poll is no longer cancelable (expiredPending)_D156', async () => {
      api.get.mockResolvedValue({
        data: pollDetail({
          organizerId: 'viewer-1',
          status: 'polling',
          deadline: new Date(Date.now() - 50 * 60 * 60_000).toISOString(),
          selectionWindowEndsAt: new Date(Date.now() - 60_000).toISOString(),
        }),
      })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Cancel poll' })).not.toBeInTheDocument()
    })

    it('a rejected (409) cancel surfaces a localized error instead of redirecting_D156', async () => {
      api.get.mockResolvedValue({ data: pollDetail({ organizerId: 'viewer-1' }) })
      const ConflictError = class extends Error {
        status = 409
        code = 'poll_invalid_transition'
      }
      api.post.mockRejectedValue(new ConflictError())
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Cancel poll' }))
      const dialog = screen.getByRole('dialog')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel poll' }))

      expect(
        await screen.findByText('That change is no longer possible.'),
      ).toBeInTheDocument()
      expect(screen.queryByTestId('screen-poll-detail')).toBeInTheDocument()
    })
  })
  describe('leaving a poll (D-186)', () => {
    function participantPoll(overrides: Partial<Record<string, unknown>> = {}) {
      return pollDetail({
        organizerId: 'organizer-1',
        roster: [
          {
            participationId: 'participation-1',
            userId: 'organizer-1',
            guestIdentityId: null,
            displayName: 'Organizer One',
            isActive: true,
          },
          {
            participationId: 'participation-9',
            userId: 'viewer-1',
            guestIdentityId: null,
            displayName: 'Viewer One',
            isActive: true,
          },
        ],
        ...overrides,
      })
    }

    it('offers the control to a participant and calls the leave endpoint_D186', async () => {
      api.get.mockResolvedValue({ data: participantPoll() })
      api.delete.mockResolvedValue({ data: undefined })
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Leave poll' }))

      await waitFor(() =>
        expect(api.delete).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/my-participation',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1' } }),
          }),
        ),
      )
    })

    it('hides the control from the organizer, whose row the poll needs_D186', async () => {
      api.get.mockResolvedValue({ data: participantPoll({ organizerId: 'viewer-1' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Leave poll' })).not.toBeInTheDocument()
    })

    it('hides the control from a viewer who is not on the roster_D186', async () => {
      api.get.mockResolvedValue({ data: pollDetail() })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Leave poll' })).not.toBeInTheDocument()
    })
  })

  it('renders an avatar disc on each poll roster row_D188', async () => {
    api.get.mockResolvedValue({
      data: pollDetail({
        roster: [
          {
            participationId: 'pp-1',
            userId: 'user-7',
            guestIdentityId: null,
            displayName: 'Ada',
            isActive: true,
            avatar: { kind: 'emoji', emoji: '🐙', avatarSourceId: null, imgUrl: null },
          },
          {
            participationId: 'pp-masked',
            userId: 'user-blocked',
            guestIdentityId: null,
            displayName: null,
            isActive: true,
            avatar: { kind: 'emoji', emoji: '⚡', avatarSourceId: null, imgUrl: null },
          },
        ],
      }),
    })
    renderPage()

    const named = await screen.findByTestId('poll-roster-entry-pp-1')
    expect(named.querySelector('[aria-hidden="true"]')).toHaveTextContent('🐙')

    const masked = screen.getByTestId('poll-roster-entry-pp-masked')
    const maskedDisc = masked.querySelector('[aria-hidden="true"]')
    expect(maskedDisc).toHaveTextContent('●')
    expect(maskedDisc).not.toHaveTextContent('⚡')
  })
})
