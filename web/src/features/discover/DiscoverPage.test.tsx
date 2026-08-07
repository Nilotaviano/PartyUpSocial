import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
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
import { DiscoverPage } from './DiscoverPage'

function session(overrides: Record<string, unknown> = {}) {
  return {
    id: 'session-1',
    organizerId: null,
    title: 'Open lobby',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'steam',
    game: null,
    status: 'unconfirmed',
    isPrivate: false,
    maxCapacity: null,
    activeOccupancy: 1,
    organizer: { ordinal: 0, displayName: null },
    viewerRelation: 'inherited',
    ...overrides,
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <DiscoverPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/sessions')
      return Promise.resolve({ data: { items: [session()], nextCursor: null } })
    throw new Error(`Unexpected GET ${path}`)
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('DiscoverPage', () => {
  it('links a discover row to the shipped detail route_D149', async () => {
    renderPage()

    const link = await screen.findByRole('link', { name: /Open lobby/ })
    expect(link).toHaveAttribute('href', '/sessions/session-1')
  })

  it('renders an unnamed organizer as Anonymous User', async () => {
    renderPage()

    expect(await screen.findByText(/Anonymous User/)).toBeInTheDocument()
  })
})
