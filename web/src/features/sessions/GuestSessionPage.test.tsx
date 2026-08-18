import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
  ApiError: class ApiError extends Error {
    status: number
    code: string
    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  },
}))

import '../../i18n'
import { GuestSessionPage } from './GuestSessionPage'

const linkId = '11111111-1111-1111-1111-111111111111'

function liveUnclaimed() {
  return {
    state: 'live',
    sessionId: 'session-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: null,
    notes: null,
    guestIdentityId: null,
    displayName: null,
    response: null,
  }
}

function liveClaimed() {
  return { ...liveUnclaimed(), guestIdentityId: 'guest-1', displayName: 'Bob', response: 'Pending' }
}

// Route-aware guest-link rejection: the session GET and the poll GET are two DISTINCT backend
// routes (`GuestSessionEndpoints`/`GuestPollEndpoints`) that fail independently — a single
// uniform mocked rejection previously hid F2 (a real dead link's poll fallback returns its own
// `poll_not_found`, per `GuestPollEndpoints.cs:79`, never `session_not_found`).
function mockGuestRoutes(sessionError: unknown, pollError: unknown = sessionError) {
  api.get.mockImplementation((path: string) =>
    path === '/api/v1/guest/{guestLinkId}/session'
      ? Promise.reject(sessionError)
      : Promise.reject(pollError),
  )
}

function renderPage(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/guest/invalid" element={<GuestSessionPage />} />
          <Route path="/guest/:guestLinkId" element={<GuestSessionPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...utils, queryClient }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('GuestSessionPage', () => {
  it('renders the invalid notice at /guest/invalid with no query and no cookie read_D044', () => {
    renderPage('/guest/invalid')

    expect(screen.getByTestId('screen-guest-invalid')).toBeInTheDocument()
    expect(screen.getByText('This link is not valid.')).toBeInTheDocument()
    expect(api.get).not.toHaveBeenCalled()
  })

  it('renders the claim form for a live, unclaimed link needing only a display name_D017', async () => {
    api.get.mockResolvedValue({ data: liveUnclaimed() })
    api.post.mockResolvedValue({ data: liveClaimed() })
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByLabelText('Display name')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Display name'), {
      target: { value: 'Bob' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Respond' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/claim',
        expect.objectContaining({
          body: { displayName: 'Bob', response: 'Pending' },
        }),
      ),
    )
  })

  it('renders live respond controls for an already-claimed guest_D126', async () => {
    api.get.mockResolvedValue({ data: liveClaimed() })
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByLabelText('Display name')).toHaveValue('Bob')
    const goingButton = screen.getByRole('button', { name: 'Going' })
    api.put.mockResolvedValue({ data: { ...liveClaimed(), response: 'Going' } })
    fireEvent.click(goingButton)

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/participation',
        expect.objectContaining({ body: { response: 'Going' } }),
      ),
    )
  })

  it.each([
    ['canceled', 'This session was canceled.'],
    ['revoked', 'This link was revoked.'],
    ['ended', 'This session has ended.'],
  ])(
    'renders the %s terminal notice with no live fields_D167',
    async (state, text) => {
      api.get.mockResolvedValue({
        data: { state, sessionId: 'session-1', title: null, startTime: null, durationMinutes: null, platform: null, game: null, notes: null, guestIdentityId: null, displayName: null, response: null },
      })
      renderPage(`/guest/${linkId}`)

      expect(await screen.findByTestId('screen-guest-terminal')).toBeInTheDocument()
      expect(screen.getByText(text)).toBeInTheDocument()
    },
  )

  it('renders the invalid notice when the credential resolves to 404 on both routes, with no alert stacked_D044_F2', async () => {
    const SessionNotFoundError = class extends Error {
      status = 404
      code = 'session_not_found'
    }
    // The poll GET is a genuinely different route (`GuestPollEndpoints.cs:79`) that 404s with
    // its own `poll_not_found` code for the same physically-nonexistent link — never
    // `session_not_found`, which only the session route ever returns.
    const PollNotFoundError = class extends Error {
      status = 404
      code = 'poll_not_found'
    }
    mockGuestRoutes(new SessionNotFoundError(), new PollNotFoundError())
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByTestId('screen-guest-invalid')).toBeInTheDocument()
    // Both a dead session-typed link and a dead poll-typed link render the same calm notice
    // with no stacked role=alert — poll_not_found is suppressed exactly like session_not_found.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('renders the browser-zone start time without a viewer timezone override_D169', async () => {
    api.get.mockResolvedValue({ data: liveUnclaimed() })
    renderPage(`/guest/${linkId}`)

    expect(
      await screen.findByText("Times are shown in your device's time zone."),
    ).toBeTruthy()
  })

  it('renders the session end time, closing the HIGH duration-never-rendered gap', async () => {
    api.get.mockResolvedValue({ data: liveUnclaimed() })
    renderPage(`/guest/${linkId}`)

    const endText = new Intl.DateTimeFormat('en', { timeStyle: 'short' }).format(
      new Date('2030-09-01T20:00:00Z'),
    )
    expect(await screen.findByText(`Ends ${endText}`)).toBeInTheDocument()
  })

  it('renders the localized message for a known error code on the invalid link', async () => {
    const ApiErrorClass = class extends Error {
      status = 429
      code = 'rate_limited'
    }
    // A rate limit is an infra-level ceiling applied uniformly ahead of both routes — realistic
    // to reject both the session and the poll GET with the identical code, unlike the domain-
    // specific 404 codes above.
    mockGuestRoutes(new ApiErrorClass())
    renderPage(`/guest/${linkId}`)

    expect(
      await screen.findByText('Too many attempts. Try again later.'),
    ).toBeInTheDocument()
  })

  it('renders the generic message for an unknown error code on the invalid link', async () => {
    const ApiErrorClass = class extends Error {
      status = 500
      code = 'totally_unknown_code'
    }
    mockGuestRoutes(new ApiErrorClass())
    renderPage(`/guest/${linkId}`)

    expect(
      await screen.findByText('Something went wrong.'),
    ).toBeInTheDocument()
  })
})

// S6 (design §5.4/§4): a poll-typed guest link resolves via the SAME route — the session
// GET structurally 404s first (session_not_found), which falls back to the poll GET.
const SessionNotFound = class extends Error {
  status = 404
  code = 'session_not_found'
}

function livePollUnclaimed() {
  return {
    state: 'live',
    pollId: 'poll-1',
    sessionId: null,
    title: 'Raid night poll',
    platform: 'steam',
    game: null,
    notes: null,
    deadline: '2099-01-02T00:00:00Z',
    selectionWindowEndsAt: '2099-01-04T00:00:00Z',
    status: 'polling',
    guestIdentityId: null,
    displayName: null,
    options: [
      {
        id: 'option-1',
        startTime: '2099-01-01T10:00:00Z',
        durationMinutes: 120,
        suggestedByUserId: null,
        suggestedByGuestIdentityId: null,
        availableCount: 0,
        maybeCount: 0,
        unavailableCount: 0,
        viewerVote: null,
      },
    ],
    roster: [],
  }
}

function livePollClaimed() {
  return {
    ...livePollUnclaimed(),
    guestIdentityId: 'guest-1',
    displayName: 'Dana',
  }
}

describe('GuestSessionPage — poll branch (S6, PHASE6_DESIGN.md §5.4)', () => {
  it('falls back to the poll view when the session credential 404s, and shows the claim form_D017', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({ data: livePollUnclaimed() }),
    )
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByTestId('screen-guest-poll')).toBeInTheDocument()
    expect(screen.getByLabelText('Your name')).toBeInTheDocument()
    expect(screen.getByTestId('vote-available-option-1')).toBeDisabled()
  })

  it('claims with a display name, then votes on the shared voting grid_D017', async () => {
    let claimed = false
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/guest/{guestLinkId}/session') {
        return Promise.reject(new SessionNotFound())
      }
      return Promise.resolve({ data: claimed ? livePollClaimed() : livePollUnclaimed() })
    })
    api.post.mockImplementation(() => {
      claimed = true
      return Promise.resolve({ data: livePollClaimed() })
    })
    renderPage(`/guest/${linkId}`)

    fireEvent.change(await screen.findByLabelText('Your name'), {
      target: { value: 'Dana' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Join and vote' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/poll/claim',
        expect.objectContaining({ body: { displayName: 'Dana' } }),
      ),
    )

    api.put.mockResolvedValue({ data: livePollClaimed() })
    await waitFor(() =>
      expect(screen.getByTestId('vote-available-option-1')).not.toBeDisabled(),
    )
    fireEvent.click(screen.getByTestId('vote-available-option-1'))

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/poll/options/{optionId}/vote',
        expect.objectContaining({ body: { choice: 'available' } }),
      ),
    )
  })

  it('sends the chosen length alongside a guest-suggested start_D211', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({ data: livePollClaimed() }),
    )
    api.post.mockResolvedValue({ data: livePollClaimed() })
    renderPage(`/guest/${linkId}`)

    fireEvent.change(await screen.findByLabelText('Suggest a time'), {
      target: { value: '2099-03-04T19:30' },
    })
    fireEvent.change(screen.getByLabelText('Length'), { target: { value: '120' } })
    fireEvent.click(screen.getByRole('button', { name: 'Suggest' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/poll/options',
        expect.objectContaining({
          body: {
            startTime: new Date('2099-03-04T19:30').toISOString(),
            durationMinutes: 120,
          },
        }),
      ),
    )
  })

  it('renders each poll option own start-to-end range_D211', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({ data: livePollClaimed() }),
    )
    renderPage(`/guest/${linkId}`)

    const row = await screen.findByTestId('poll-option-option-1')
    const time = (iso: string) =>
      new Intl.DateTimeFormat('en', { timeStyle: 'short' }).format(new Date(iso))
    // The fixture option is 120 minutes long, so its end has to be on the row.
    expect(row.textContent).toContain(time('2099-01-01T12:00:00Z'))
  })

  it('a claimed guest renames themselves (D-044 guest arm)', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({ data: livePollClaimed() }),
    )
    api.put.mockResolvedValue({ data: { ...livePollClaimed(), displayName: 'Dana R' } })
    renderPage(`/guest/${linkId}`)

    const renameField = await screen.findByLabelText('Your name')
    fireEvent.change(renameField, { target: { value: 'Dana R' } })
    fireEvent.click(screen.getByRole('button', { name: 'Join and vote' }))

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/guest/{guestLinkId}/poll/display-name',
        expect.objectContaining({ body: { displayName: 'Dana R' } }),
      ),
    )
  })

  it.each([
    ['canceled', 'This poll was canceled.'],
    ['revoked', 'This invitation link was revoked.'],
    ['pollExpired', "This poll's selection window has closed."],
  ])('renders the %s poll terminal notice_D126', async (state, text) => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({
            data: { state, pollId: 'poll-1', sessionId: null, title: null, durationMinutes: null, platform: null, game: null, notes: null, deadline: null, selectionWindowEndsAt: null, status: null, guestIdentityId: null, displayName: null, options: null, roster: null },
          }),
    )
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByTestId('screen-guest-terminal')).toBeInTheDocument()
    expect(screen.getByText(text)).toBeInTheDocument()
  })

  it('renders the "re-open your invitation link" copy for a finalized poll — no re-exchange URL field', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({
            data: { state: 'finalized', pollId: 'poll-1', sessionId: 'session-9', title: null, durationMinutes: null, platform: null, game: null, notes: null, deadline: null, selectionWindowEndsAt: null, status: null, guestIdentityId: null, displayName: null, options: null, roster: null },
          }),
    )
    renderPage(`/guest/${linkId}`)

    expect(
      await screen.findByText(
        'This poll was finalized. Re-open your invitation link for the session.',
      ),
    ).toBeInTheDocument()
  })
})

describe('GuestSessionPage — D-217 anonymous feedback entry point', () => {
  it('offers a discreet feedback link on the live guest RSVP arm_D217', async () => {
    api.get.mockResolvedValue({ data: liveClaimed() })
    renderPage(`/guest/${linkId}`)

    const link = await screen.findByRole('button', { name: 'Send feedback' })
    expect(link).toBeInTheDocument()

    // It never competes with the RSVP the page exists for: it comes after those controls.
    const rsvp = screen.getByRole('button', { name: 'Going' })
    expect(rsvp.compareDocumentPosition(link) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('offers the same feedback link on the live guest poll-vote arm_D217', async () => {
    api.get.mockImplementation((path: string) =>
      path === '/api/v1/guest/{guestLinkId}/session'
        ? Promise.reject(new SessionNotFound())
        : Promise.resolve({ data: livePollUnclaimed() }),
    )
    renderPage(`/guest/${linkId}`)

    await screen.findByTestId('screen-guest-poll')
    expect(screen.getByRole('button', { name: 'Send feedback' })).toBeInTheDocument()
  })

  it('opens the feedback dialog from the guest page with no account_D217', async () => {
    api.get.mockResolvedValue({ data: liveClaimed() })
    renderPage(`/guest/${linkId}`)

    fireEvent.click(await screen.findByRole('button', { name: 'Send feedback' }))

    expect(await screen.findByRole('heading', { name: 'Send feedback' })).toBeInTheDocument()
    expect(screen.getByLabelText('Your message')).toBeInTheDocument()
  })
})
