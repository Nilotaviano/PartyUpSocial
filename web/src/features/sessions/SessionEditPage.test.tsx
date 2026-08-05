import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  patch: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PATCH: api.patch },
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

import { ApiError } from '../../api/client'
import '../../i18n'
import { SessionEditPage } from './SessionEditPage'

function detail(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    notes: 'Bring snacks',
    minAttendance: 4,
    maxCapacity: 8,
    isPrivate: false,
    status: 'Unconfirmed',
    createdAt: '2030-08-01T00:00:00Z',
    roster: [],
    version: 'v1',
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/sessions/session-1/edit']}>
        <Routes>
          <Route path="/sessions/:sessionId/edit" element={<SessionEditPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockResolvedValue({ data: detail() })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionEditPage', () => {
  it('maps the clear-max/notes/game checkboxes onto the clear list_D121', async () => {
    api.patch.mockResolvedValue({ data: detail({ maxCapacity: null, notes: null, game: null }) })
    renderPage()

    fireEvent.click(await screen.findByLabelText('Remove capacity limit'))
    fireEvent.click(screen.getByLabelText('Remove notes'))
    fireEvent.click(screen.getByLabelText('Remove game'))
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            clear: expect.arrayContaining(['maxCapacity', 'notes', 'game']),
          }),
        }),
      ),
    )
  })

  it('shows the silent-change disclosure next to the privacy control_D123', async () => {
    renderPage()

    expect(await screen.findByText('This change does not notify participants.')).toBeInTheDocument()
  })

  it('retries exactly once after a session_conflict then surfaces the error on repeat failure_D121', async () => {
    api.patch
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
    api.get.mockResolvedValue({ data: detail() })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(2))
    expect(
      await screen.findByText(
        'This session changed elsewhere. We refreshed it and are retrying your save.',
      ),
    ).toBeInTheDocument()
    expect(await screen.findByText('This session changed elsewhere. Please try again.')).toBeInTheDocument()
  })

  it('succeeds after exactly one automatic refetch-and-retry on conflict_D121', async () => {
    api.get
      .mockResolvedValueOnce({ data: detail({ version: 'v1' }) })
      .mockResolvedValueOnce({ data: detail({ version: 'v2' }) })
    api.patch
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
      .mockResolvedValueOnce({ data: detail({ version: 'v2' }) })
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(2))
    expect(api.patch.mock.calls[1][1].body.ifMatch).toBe('v2')
  })
})
