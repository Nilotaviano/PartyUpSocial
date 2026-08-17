import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() }))
const session = vi.hoisted(() => ({ data: { userId: 'viewer-1' } }))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
  isApiError: (error: unknown) => Boolean(error && typeof error === 'object' && 'code' in error),
}))

vi.mock('../../../auth/useSession', () => ({
  useSession: () => session,
}))

import { BOTTOM_BAR_OFFSET_CLASSES } from '../../../components/shell/bottomBarOffset'
import '../../../i18n'
import { PollDetailPage } from './PollDetailPage'

function pollDetail(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'poll-1',
    organizerId: 'organizer-1',
    title: 'Raid night poll',
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
        durationMinutes: 120,
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 3,
        maybeCount: 1,
        unavailableCount: 0,
        viewerVote: 'available',
        availableVoters: [],
        maybeVoters: [],
        unavailableVoters: [],
      },
      {
        id: 'option-2',
        // D-140: an already-elapsed option, alongside the future one above.
        startTime: '2020-01-01T10:00:00Z',
        durationMinutes: 120,
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 2,
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

function mockGet({
  detail = pollDetail(),
  preview = { options: [] } as unknown,
  timeZone = 'UTC',
} = {}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone } })
    if (path.includes('finalization-preview')) return Promise.resolve({ data: preview })
    return Promise.resolve({ data: detail })
  })
}

function renderPage(pollId = 'poll-1', state?: unknown) {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter
        initialEntries={[{ pathname: `/polls/${pollId}`, state }]}
      >
        <Routes>
          <Route element={<PollDetailPage />} path="/polls/:pollId" />
          <Route element={<div data-testid="navigated-to-polls" />} path="/polls" />
          {/* D-160: a real route the finalize navigation must actually land on. */}
          <Route element={<div data-testid="navigated-to-session" />} path="/sessions/:sessionId" />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollDetailPage (lovable)', () => {
  it('renders option tallies and the viewer own vote state_D140', async () => {
    mockGet()
    renderPage()

    const futureOption = await screen.findByTestId('poll-option-option-1')
    expect(within(futureOption).getByTestId('vote-available-option-1')).toHaveAttribute(
      'aria-checked',
      'true',
    )
  })

  it('renders an elapsed option ordinarily, marked "already passed" but never dropped_D140', async () => {
    mockGet()
    renderPage()

    const pastOption = await screen.findByTestId('poll-option-option-2')
    expect(within(pastOption).getByText('Already passed')).toBeInTheDocument()
    expect(within(pastOption).getByTestId('vote-available-option-2')).toHaveAttribute(
      'aria-checked',
      'false',
    )
  })

  /**
   * Ported from the pre-port PollAccessibility.test.tsx. STYLING_OVERHAUL_DESIGN.md
   * §3.2's S4 duty extension, scoped to `role=radio` *button* controls (the vote
   * controls here).
   */
  it('every role=radio vote control meets the 44px touch target and visible-focus duties_D139', async () => {
    mockGet()
    renderPage()
    await screen.findByTestId('vote-available-option-1')

    const voteControls = screen.getAllByRole('radio').filter((control) => control.tagName === 'BUTTON')

    expect(voteControls).toHaveLength(6)
    for (const control of voteControls) {
      expect(control).toHaveClass('min-h-11')
      expect(control).toHaveClass('min-w-11')
      expect(control.className).toContain('focus-visible:outline-2')
      expect(control.className).toContain('focus-visible:outline-offset-2')
      expect(control.className).toContain('focus-visible:outline-focus')
    }
  })

  it('focuses the page h1 on mount_D139', async () => {
    mockGet()
    renderPage()

    const heading = await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(heading).toHaveFocus())
  })

  it('maps the notApplicable wire platform value to a localized "Other" label, not the raw wire value', async () => {
    mockGet({ detail: pollDetail({ platform: 'notApplicable' }) })
    renderPage()

    await screen.findByText('Raid night poll')
    expect(screen.queryByText('notApplicable')).not.toBeInTheDocument()
    expect(screen.getByText('Other')).toBeInTheDocument()
  })

  /**
   * Ported from the pre-port PollAccessibility.test.tsx. The option row's chrome
   * must not encode the viewer's own vote: a voted row and an unvoted one carry
   * byte-identical class lists, and the vote itself is exposed via aria-checked
   * on the radio control instead (covered by the D-140 tests above).
   */
  it('an option row class list is identical whether or not the viewer has voted on it_D140', async () => {
    mockGet()
    renderPage()

    const voted = await screen.findByTestId('poll-option-option-1')
    const unvoted = screen.getByTestId('poll-option-option-2')

    expect(unvoted.className).toBe(voted.className)
  })

  it('masks a roster entry with no display name as Anonymous User_D076', async () => {
    mockGet()
    renderPage()

    const organizerRow = await screen.findByTestId('poll-roster-entry-participation-1')
    expect(within(organizerRow).getAllByText('Organizer One').length).toBeGreaterThan(0)
    const maskedRow = screen.getByTestId('poll-roster-entry-participation-2')
    expect(within(maskedRow).getAllByText('Anonymous User').length).toBeGreaterThan(0)
  })

  /** D-211: the poll has no length of its own; every option states its own. */
  function utcTime(iso: string) {
    return new Intl.DateTimeFormat('en', {
      hour: 'numeric',
      minute: '2-digit',
      timeZone: 'UTC',
    }).format(new Date(iso))
  }

  it('renders every option own start-to-end range, never one shared length_D211', async () => {
    mockGet({
      detail: pollDetail({
        options: [
          { ...pollDetail().options[0], durationMinutes: 60 },
          { ...pollDetail().options[1], durationMinutes: 180 },
        ],
      }),
    })
    renderPage()

    const first = await screen.findByTestId('poll-option-option-1')
    expect(first.textContent).toContain(
      `${utcTime('2099-01-01T10:00:00Z')}–${utcTime('2099-01-01T11:00:00Z')}`,
    )
    const second = screen.getByTestId('poll-option-option-2')
    expect(second.textContent).toContain(
      `${utcTime('2020-01-01T10:00:00Z')}–${utcTime('2020-01-01T13:00:00Z')}`,
    )
  })

  it('drops the poll-wide Duration summary row_D211', async () => {
    mockGet()
    renderPage()

    await screen.findByText('Raid night poll')
    expect(within(screen.getByTestId('poll-summary-card')).queryByText('Duration')).toBeNull()
  })

  it('sends the chosen length alongside a suggested start_D211', async () => {
    mockGet()
    api.post.mockResolvedValue({ data: {} })
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Suggest a time' }))
    fireEvent.change(screen.getByLabelText('Suggest a time'), {
      target: { value: '2099-03-04T19:30' },
    })
    fireEvent.change(screen.getByLabelText('Length'), { target: { value: '90' } })
    fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

    await waitFor(() => expect(api.post).toHaveBeenCalled())
    const [path, options] = api.post.mock.calls.at(-1) as [
      string,
      { body: { startTime: string; durationMinutes: number } },
    ]
    expect(path).toBe('/api/v1/polls/{pollId}/options')
    expect(options.body.durationMinutes).toBe(90)
    expect(options.body.startTime).toBe(new Date('2099-03-04T19:30').toISOString())
  })

  it('renders a small "Suggested by" attribution line for an option with a resolved suggester_D196', async () => {
    mockGet({
      detail: pollDetail({
        options: [
          {
            id: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            durationMinutes: 120,
            suggestedByUserId: 'participation-2-user',
            suggestedByGuestIdentityId: null,
            suggester: {
              participationId: 'participation-2',
              userId: 'participation-2-user',
              guestIdentityId: null,
              displayName: 'Suggester Sam',
              isActive: true,
              avatar: null,
            },
            availableCount: 0,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }),
    })
    renderPage()

    const option = await screen.findByTestId('poll-option-suggested-by-option-1')
    expect(option).toHaveTextContent('Suggested by Suggester Sam')
  })

  it('renders the masked Anonymous User attribution for a blocked-pair suggester_D196', async () => {
    mockGet({
      detail: pollDetail({
        options: [
          {
            id: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            durationMinutes: 120,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            suggester: {
              participationId: 'participation-2',
              userId: null,
              guestIdentityId: null,
              displayName: null,
              isActive: true,
              avatar: null,
            },
            availableCount: 0,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }),
    })
    renderPage()

    const option = await screen.findByTestId('poll-option-suggested-by-option-1')
    expect(option).toHaveTextContent('Suggested by Anonymous User')
  })

  it('renders no attribution line at all for an organizer-created option_D196', async () => {
    mockGet()
    renderPage()

    await screen.findByTestId('poll-option-option-1')
    expect(screen.queryByTestId('poll-option-suggested-by-option-1')).not.toBeInTheDocument()
    expect(screen.queryByTestId('poll-option-suggested-by-option-2')).not.toBeInTheDocument()
  })

  it('renders an avatar disc on each poll roster row, masked without the real emoji_D188', async () => {
    mockGet({
      detail: pollDetail({
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

  it('renders per-vote-category voter chips from the mapped lists, masked voter shows the anonymous label_D076', async () => {
    mockGet({
      detail: pollDetail({
        options: [
          {
            id: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            durationMinutes: 120,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 2,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: 'available',
            availableVoters: [
              {
                participationId: 'participation-1',
                userId: 'organizer-1',
                guestIdentityId: null,
                displayName: 'Organizer One',
                isActive: true,
                avatar: null,
              },
              {
                participationId: 'participation-2',
                userId: null,
                guestIdentityId: null,
                displayName: null,
                isActive: true,
                avatar: null,
              },
            ],
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }),
    })
    renderPage()

    const chips = await screen.findByTestId('poll-option-voters-option-1-available')
    expect(within(chips).getAllByText('Organizer One').length).toBeGreaterThan(0)
    expect(chips).toHaveAttribute('aria-label', expect.stringContaining('Anonymous User'))
    expect(within(chips).getAllByText('Anonymous User').length).toBeGreaterThan(0)
  })

  it('shows a +N overflow pill when a category has more voters than the chip collapse count', async () => {
    const manyVoters = Array.from({ length: 7 }, (_, index) => ({
      participationId: `voter-${index}`,
      userId: `user-${index}`,
      guestIdentityId: null,
      displayName: `Voter ${index}`,
      isActive: true,
      avatar: null,
    }))
    mockGet({
      detail: pollDetail({
        options: [
          {
            id: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            durationMinutes: 120,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 7,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: manyVoters,
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }),
    })
    renderPage()

    const chips = await screen.findByTestId('poll-option-voters-option-1-available')
    expect(within(chips).getByText('+2')).toBeInTheDocument()
  })

  it('renders the server-aggregated count even when it disagrees with the voter list length (defensive)', async () => {
    mockGet({
      detail: pollDetail({
        options: [
          {
            id: 'option-1',
            startTime: '2099-01-01T10:00:00Z',
            durationMinutes: 120,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 9,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          },
        ],
      }),
    })
    renderPage()

    const option = await screen.findByTestId('poll-option-option-1')
    expect(within(option).getByText('Available 9')).toBeInTheDocument()
  })

  it('mutes an unmuted, non-terminal poll via PUT_D090', async () => {
    mockGet({ detail: pollDetail({ status: 'polling' }) })
    api.put.mockResolvedValue({ data: {} })
    renderPage()

    const toggle = await screen.findByRole('button', { name: 'Mute' })
    expect(toggle).not.toBeDisabled()
    fireEvent.click(toggle)

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/polls/{pollId}/mute',
        expect.objectContaining({ params: expect.objectContaining({ path: { pollId: 'poll-1' } }) }),
      ),
    )
  })

  it('unmutes a muted poll via DELETE_D090', async () => {
    mockGet({ detail: pollDetail({ status: 'polling', muted: true }) })
    api.delete.mockResolvedValue({})
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Unmute' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/polls/{pollId}/mute',
        expect.objectContaining({ params: expect.objectContaining({ path: { pollId: 'poll-1' } }) }),
      ),
    )
  })

  it.each([
    ['mute', false, 'Mute', 'Poll muted.'],
    ['unmute', true, 'Unmute', 'Poll unmuted.'],
  ])(
    'announces the %s outcome in a polite live region_D090_D139',
    async (_action, muted, buttonName, announcement) => {
      mockGet({ detail: pollDetail({ status: 'polling', muted }) })
      api.put.mockResolvedValue({ data: {} })
      api.delete.mockResolvedValue({})
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: buttonName }))

      const announced = await screen.findByText(announcement)
      expect(announced).toHaveAttribute('aria-live', 'polite')
    },
  )

  it.each(['finalized', 'expired', 'canceled'])(
    'disables the mute toggle on a terminal (%s) poll_D113',
    async (status) => {
      mockGet({ detail: pollDetail({ status }) })
      renderPage()

      expect(await screen.findByRole('button', { name: 'Mute' })).toBeDisabled()
    },
  )

  it('links a finalized poll to its created session_D160', async () => {
    mockGet({
      detail: pollDetail({ status: 'finalized', createdSessionId: 'session-9', selectedOptionId: 'option-1' }),
    })
    renderPage()

    expect(await screen.findByRole('link', { name: 'View the session' })).toHaveAttribute(
      'href',
      '/sessions/session-9',
    )
  })

  it('renders an expired countdown banner', async () => {
    mockGet({ detail: pollDetail({ status: 'expired' }) })
    renderPage()

    expect(
      await screen.findByText('Expired — the 48h picking window passed without a choice.'),
    ).toBeInTheDocument()
  })

  it('renders a canceled countdown banner', async () => {
    mockGet({ detail: pollDetail({ status: 'canceled' }) })
    renderPage()

    expect(
      await screen.findByText('Cancelled by the organizer. Votes are kept for reference.'),
    ).toBeInTheDocument()
  })

  describe('voting', () => {
    it('submits a per-option vote call, not a batch ballot_D172', async () => {
      mockGet()
      api.put.mockResolvedValue({ data: pollDetail() })
      renderPage()

      const pastOption = await screen.findByTestId('poll-option-option-2')
      fireEvent.click(within(pastOption).getByTestId('vote-maybe-option-2'))

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

    it('disables the vote controls once voting has closed_D140', async () => {
      mockGet({
        detail: pollDetail({ status: 'closed', deadline: new Date(Date.now() - 60_000).toISOString() }),
      })
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      expect(within(option).getByTestId('vote-available-option-1')).toBeDisabled()
    })
  })

  describe('suggest option (D-033/D-171)', () => {
    it('suggests an option through the dialog', async () => {
      mockGet()
      api.post.mockResolvedValue({ data: pollDetail() })
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Suggest a time' }))
      fireEvent.change(await screen.findByLabelText('Suggest a time'), {
        target: { value: '2099-03-01T10:00' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/options',
          expect.objectContaining({ params: expect.objectContaining({ path: { pollId: 'poll-1' } }) }),
        ),
      )
    })

    it('renders the D-171 suggestion-cap copy, distinct from the D-131 creation-cap copy_D171', async () => {
      const ApiErrorClass = class extends Error {
        status = 422
        code = 'poll_option_cap'
      }
      mockGet({
        detail: pollDetail({
          options: Array.from({ length: 12 }, (_, index) => ({
            id: `option-${index + 1}`,
            startTime: `2099-01-0${(index % 9) + 1}T10:00:00Z`,
            durationMinutes: 120,
            suggestedByUserId: null,
            suggestedByGuestIdentityId: null,
            availableCount: 0,
            maybeCount: 0,
            unavailableCount: 0,
            viewerVote: null,
            availableVoters: [],
            maybeVoters: [],
            unavailableVoters: [],
          })),
        }),
      })
      api.post.mockRejectedValue(new ApiErrorClass())
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Suggest a time' }))
      fireEvent.change(await screen.findByLabelText('Suggest a time'), {
        target: { value: '2099-03-01T10:00' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

      expect(
        await screen.findByText('This poll already has 12 candidate times — the most it can hold.'),
      ).toBeInTheDocument()
    })

    it('routes a non-cap suggest error into the still-open dialog, visible above the scrim (Wave 6)', async () => {
      const ApiErrorClass = class extends Error {
        status = 500
        code = 'unknown'
      }
      mockGet()
      api.post.mockRejectedValue(new ApiErrorClass())
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Suggest a time' }))
      fireEvent.change(await screen.findByLabelText('Suggest a time'), {
        target: { value: '2099-03-01T10:00' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

      const dialog = await screen.findByRole('dialog')
      expect(within(dialog).getByRole('alert')).toBeInTheDocument()
      // Still open — the error did not silently fall through to a hidden
      // page-level banner behind the dialog's scrim.
      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
  })

  describe('delete option (new real capability behind D-131/171 scope)', () => {
    it('deletes an option behind a confirmation dialog, via the real DeletePollOption endpoint', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })
      api.delete.mockResolvedValue({})
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      fireEvent.click(within(option).getByRole('button', { name: 'Delete option' }))
      const dialog = screen.getByRole('dialog')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Delete option' }))

      await waitFor(() =>
        expect(api.delete).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/options/{optionId}',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1', optionId: 'option-1' } }),
          }),
        ),
      )
    })

    it('hides delete/preview controls once the poll is finalized', async () => {
      mockGet({
        detail: pollDetail({ organizerId: 'viewer-1', status: 'finalized', selectedOptionId: 'option-1' }),
      })
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      expect(within(option).queryByRole('button', { name: 'Delete option' })).not.toBeInTheDocument()
    })
  })

  describe('organizer guest panel (D-044)', () => {
    it('surfaces guest links minted at poll creation, handed off via one-shot router state', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })
      renderPage('poll-1', {
        mintedGuestLinks: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
      })

      expect(await screen.findByText('https://partyup.social/g/abc')).toBeInTheDocument()
      expect(
        screen.getByText(
          "These links were just created. Save them now — they can't be shown again after you leave this page.",
        ),
      ).toBeInTheDocument()
      expect(api.post).not.toHaveBeenCalled()
    })

    it('clears the router history state after intaking mintedGuestLinks, so refresh/session-restore cannot resurface the tokens_F1', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })

      let latestLocationState: unknown = 'not-yet-observed'
      function LocationProbe() {
        latestLocationState = useLocation().state
        return null
      }

      render(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <MemoryRouter
            initialEntries={[
              {
                pathname: '/polls/poll-1',
                state: {
                  mintedGuestLinks: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
                },
              },
            ]}
          >
            <Routes>
              <Route
                element={
                  <>
                    <PollDetailPage />
                    <LocationProbe />
                  </>
                }
                path="/polls/:pollId"
              />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>,
      )

      // The links still surface from the captured component state...
      expect(await screen.findByText('https://partyup.social/g/abc')).toBeInTheDocument()
      // ...but the history entry's own state is replaced with null so a
      // refresh/session-restore cannot resurface the minted tokens.
      await waitFor(() => expect(latestLocationState).toBeNull())
    })

    it('mints a guest link, shows it plaintext-once, and lets the organizer revoke it', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })
      api.post.mockResolvedValue({
        data: [{ guestLinkId: 'link-1', url: 'https://partyup.social/g/abc' }],
      })
      api.delete.mockResolvedValue({})
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Create link' }))
      expect(await screen.findByText('https://partyup.social/g/abc')).toBeInTheDocument()

      fireEvent.click(screen.getByRole('button', { name: 'Revoke' }))
      await waitFor(() =>
        expect(api.delete).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/guest-links/{guestLinkId}',
          expect.objectContaining({
            params: expect.objectContaining({ path: { pollId: 'poll-1', guestLinkId: 'link-1' } }),
          }),
        ),
      )
      expect(screen.queryByText('https://partyup.social/g/abc')).not.toBeInTheDocument()
    })

    it('removes a claimed guest voter behind a confirmation dialog_D044', async () => {
      mockGet({
        detail: pollDetail({
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
            params: expect.objectContaining({ path: { pollId: 'poll-1', guestIdentityId: 'guest-9' } }),
          }),
        ),
      )
    })

    it('hides the guest panel from a non-organizer viewer', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'someone-else' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByText('Guest links')).not.toBeInTheDocument()
    })
  })

  describe('selection/finalize preview (D-052/D-187/D-188)', () => {
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
            durationMinutes: 120,
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

    it('lifts the sticky preview bar above the mobile tab bar', async () => {
      mockGet({ detail: awaitingSelectionPoll(), preview: previewResponse() })
      renderPage()

      const bar = (await screen.findByTestId('finalize-trigger')).closest('div.fixed')

      expect(bar).not.toBeNull()
      for (const offsetClass of BOTTOM_BAR_OFFSET_CLASSES.split(' ')) {
        expect(bar).toHaveClass(offsetClass)
      }
    })

    it('finalizes the leader from the sticky preview bar and navigates to the created session_D160', async () => {
      mockGet({ detail: awaitingSelectionPoll(), preview: previewResponse() })
      api.post.mockResolvedValue({
        data: { sessionId: 'session-42', poll: awaitingSelectionPoll({ status: 'finalized' }) },
      })
      renderPage()

      fireEvent.click(await screen.findByTestId('finalize-trigger'))
      fireEvent.click(await screen.findByTestId('finalize-confirm'))

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

    it('shows seated and waitlisted rows from the finalization preview seats_D052', async () => {
      mockGet({ detail: awaitingSelectionPoll(), preview: previewResponse() })
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      const previewButton = within(option).getByRole('button', { name: 'Preview seats' })
      await waitFor(() => expect(previewButton).not.toBeDisabled())
      fireEvent.click(previewButton)

      const dialog = await screen.findByRole('dialog')
      expect(within(dialog).getByText('Organizer One')).toBeInTheDocument()
      expect(within(dialog).getByText('Voter Two')).toBeInTheDocument()
      expect(within(dialog).getByText(/Seated/)).toBeInTheDocument()
      expect(within(dialog).getByText(/Waitlist/)).toBeInTheDocument()
    })

    it('surfaces a finalization-preview fetch failure in the page error banner (Wave 6)', async () => {
      const ApiErrorClass = class extends Error {
        status = 500
        code = 'unknown'
      }
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: 'UTC' } })
        if (path.includes('finalization-preview')) return Promise.reject(new ApiErrorClass())
        return Promise.resolve({ data: awaitingSelectionPoll() })
      })
      renderPage()

      expect(await screen.findByText('Something went wrong.')).toBeInTheDocument()
    })

    it('disables the preview-seats and finalize-trigger buttons while the preview query is pending (Wave 6)', async () => {
      let resolvePreview: (() => void) | undefined
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: 'UTC' } })
        if (path.includes('finalization-preview'))
          return new Promise((resolve) => {
            resolvePreview = () => resolve({ data: previewResponse() })
          })
        return Promise.resolve({ data: awaitingSelectionPoll() })
      })
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      const previewButton = within(option).getByRole('button', { name: 'Preview seats' })
      expect(previewButton).toBeDisabled()
      expect(previewButton).toHaveAttribute('aria-busy', 'true')

      const finalizeTrigger = screen.queryByTestId('finalize-trigger')
      if (finalizeTrigger) {
        expect(finalizeTrigger).toBeDisabled()
      }

      resolvePreview?.()
    })

    it('offers no early finalization sticky bar while no option is viable_D187', async () => {
      mockGet({
        detail: pollDetail({
          organizerId: 'viewer-1',
          status: 'polling',
          deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
        }),
        preview: {
          options: [{ optionId: 'option-1', startTime: '2099-01-01T10:00:00Z', meetsMinimum: false, roster: [] }],
        },
      })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByTestId('finalize-trigger')).not.toBeInTheDocument()
    })

    it('offers early finalization once an option is viable while still polling_D187', async () => {
      mockGet({
        detail: pollDetail({
          organizerId: 'viewer-1',
          status: 'polling',
          deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
        }),
        preview: previewResponse(),
      })
      renderPage()

      expect(await screen.findByTestId('finalize-trigger')).toBeInTheDocument()
      fireEvent.click(screen.getByTestId('finalize-trigger'))

      expect(await screen.findByTestId('finalize-early-notice')).toBeInTheDocument()
      expect(screen.getByText('End voting and finalize?')).toBeInTheDocument()
    })

    it('renders a disc per finalization preview seat without rewriting anything into the seat name_D188', async () => {
      const preview = previewResponse()
      preview.options[0]!.roster = preview.options[0]!.roster.map((row) => ({
        ...row,
        avatar: { kind: 'emoji', emoji: '🦄', avatarSourceId: null, imgUrl: null },
      }))
      mockGet({ detail: awaitingSelectionPoll(), preview })
      renderPage()

      const option = await screen.findByTestId('poll-option-option-1')
      const previewButton = within(option).getByRole('button', { name: 'Preview seats' })
      await waitFor(() => expect(previewButton).not.toBeDisabled())
      fireEvent.click(previewButton)

      const dialog = await screen.findByRole('dialog')
      const discs = within(dialog).getAllByText('🦄')
      expect(discs.length).toBeGreaterThan(0)
      expect(within(dialog).getByText('Organizer One')).toBeInTheDocument()
    })
  })

  describe('organizer mid-flight invite link (D-193)', () => {
    it('shows the invite link for the organizer while Polling_D193', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1', status: 'polling' }) })
      renderPage()

      expect(await screen.findByRole('link', { name: 'Invite' })).toHaveAttribute(
        'href',
        '/polls/poll-1/invite',
      )
    })

    it('never shows the invite link to a non-organizer_D193', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'someone-else', status: 'polling' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('link', { name: 'Invite' })).not.toBeInTheDocument()
    })

    it('hides the invite link once the poll has left Polling (Closed)_D193', async () => {
      mockGet({
        detail: pollDetail({
          organizerId: 'viewer-1',
          status: 'closed',
          deadline: new Date(Date.now() - 60_000).toISOString(),
        }),
      })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('link', { name: 'Invite' })).not.toBeInTheDocument()
    })
  })

  describe('organizer cancel (D-156)', () => {
    it('an organizer confirms cancel, POSTs to the endpoint and lands on /polls_D156', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })
      api.post.mockResolvedValue({})
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Cancel poll' }))
      const dialog = screen.getByRole('dialog')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel poll' }))

      await waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/cancel',
          expect.objectContaining({ params: expect.objectContaining({ path: { pollId: 'poll-1' } }) }),
        ),
      )
      expect(await screen.findByTestId('navigated-to-polls')).toBeInTheDocument()
    })

    it('never shows the cancel control to a non-organizer_D156', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'someone-else' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Cancel poll' })).not.toBeInTheDocument()
    })

    it('hides the cancel control once the poll is no longer cancelable (expiredPending)_D156', async () => {
      mockGet({
        detail: pollDetail({
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
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1' }) })
      const ConflictError = class extends Error {
        status = 409
        code = 'poll_invalid_transition'
      }
      api.post.mockRejectedValue(new ConflictError())
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Cancel poll' }))
      const dialog = screen.getByRole('dialog')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel poll' }))

      expect(await screen.findByText('That change is no longer possible.')).toBeInTheDocument()
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
      mockGet({ detail: participantPoll() })
      api.delete.mockResolvedValue({ data: undefined })
      renderPage()

      fireEvent.click(await screen.findByRole('button', { name: 'Leave poll' }))

      await waitFor(() =>
        expect(api.delete).toHaveBeenCalledWith(
          '/api/v1/polls/{pollId}/my-participation',
          expect.objectContaining({ params: expect.objectContaining({ path: { pollId: 'poll-1' } }) }),
        ),
      )
    })

    it('hides the control from the organizer, whose row the poll needs_D186', async () => {
      mockGet({ detail: participantPoll({ organizerId: 'viewer-1' }) })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Leave poll' })).not.toBeInTheDocument()
    })

    it('hides the leave control from a viewer who is not on the roster_D186', async () => {
      mockGet({ detail: pollDetail() })
      renderPage()

      await screen.findByText('Raid night poll')
      expect(screen.queryByRole('button', { name: 'Leave poll' })).not.toBeInTheDocument()
    })
  })

  describe('mobile sidebar stacking order (F2)', () => {
    it('gives every sidebar card its own order class, in the intended below-lg stacking order', async () => {
      mockGet({ detail: pollDetail({ organizerId: 'viewer-1', status: 'polling' }) })
      api.post.mockResolvedValue({ data: [] })
      renderPage()

      await screen.findByText('Raid night poll')
      await screen.findByRole('heading', { name: 'Guest links' })

      function headingOrderOf(name: string | RegExp) {
        return screen
          .getByRole('heading', { name })
          .closest('section')?.className.match(/\border-(\d+)\b/)?.[1]
      }

      const summaryOrder = screen
        .getByTestId('poll-summary-card')
        .className.match(/\border-(\d+)\b/)?.[1]
      const invitedOrder = headingOrderOf(/^Invited/)
      const guestsOrder = headingOrderOf('Guest links')
      const notificationsOrder = headingOrderOf('Notifications for this poll')
      const dangerOrder = headingOrderOf('Danger zone')

      // Every card has an explicit order class (none silently falls back to order:0).
      for (const value of [
        summaryOrder,
        invitedOrder,
        guestsOrder,
        notificationsOrder,
        dangerOrder,
      ]) {
        expect(value).toBeDefined()
      }

      // Candidate times is a sibling `order-2` section outside this sidebar wrapper (unchanged
      // by this fix) — summary(1) -> candidate times(2) -> invited -> guests -> the rest, and no
      // card ties with the candidate-times column at order-2.
      expect(Number(summaryOrder)).toBe(1)
      expect(Number(invitedOrder)).toBeGreaterThan(2)
      expect(Number(guestsOrder)).toBeGreaterThan(Number(invitedOrder))
      expect(Number(notificationsOrder)).toBeGreaterThan(Number(guestsOrder))
      expect(Number(dangerOrder)).toBeGreaterThan(Number(notificationsOrder))
    })
  })
})
