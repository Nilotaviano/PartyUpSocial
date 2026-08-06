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

  it('renders the invalid notice when the credential resolves to 404_D044', async () => {
    const ApiErrorClass = class extends Error {
      status = 404
      code = 'session_not_found'
    }
    api.get.mockRejectedValue(new ApiErrorClass())
    renderPage(`/guest/${linkId}`)

    expect(await screen.findByTestId('screen-guest-invalid')).toBeInTheDocument()
  })

  it('renders the browser-zone start time without a viewer timezone override_D169', async () => {
    api.get.mockResolvedValue({ data: liveUnclaimed() })
    renderPage(`/guest/${linkId}`)

    expect(
      await screen.findByText("Times are shown in your device's time zone."),
    ).toBeTruthy()
  })
})
