import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

class TestApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string) {
    super(code)
    this.status = status
    this.code = code
  }
}

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) => error instanceof TestApiError,
}))

import '../../i18n'
import { AdminUsersPage } from './AdminUsersPage'

function adminUser(overrides: Record<string, unknown> = {}) {
  return {
    id: '0198c2f0-0000-7000-8000-000000000001',
    username: 'ripley',
    displayName: 'Ellen Ripley',
    email: 'e***@n***.example',
    created: '2026-01-04T09:00:00Z',
    lifecycle: 'active',
    bannedAt: null,
    lastSeenAt: '2026-08-10T18:30:00Z',
    sessionsOrganized: 5,
    sessionsJoined: 12,
    friendCount: 7,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <AdminUsersPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AdminUsersPage — D-179 list rendering', () => {
  it('renders the widened per-user projection for every listed account_D179', async () => {
    api.get.mockResolvedValue({
      data: { users: [adminUser()], page: 1, pageSize: 50, total: 1 },
    })

    renderPage()

    expect(await screen.findByText('Ellen Ripley')).toBeTruthy()
    expect(screen.getByText('ripley')).toBeTruthy()
    // The email arrives already masked from the server and is rendered as-is.
    expect(screen.getByText('e***@n***.example')).toBeTruthy()
    expect(screen.getByText('Registered')).toBeTruthy()
    expect(screen.getByText('Last activity')).toBeTruthy()
    expect(screen.getByText('5')).toBeTruthy()
    expect(screen.getByText('12')).toBeTruthy()
    expect(screen.getByText('7')).toBeTruthy()
    expect(screen.getByText('Not banned')).toBeTruthy()
  })

  it('reports a banned account and an account that has never been seen_D179', async () => {
    api.get.mockResolvedValue({
      data: {
        users: [adminUser({ bannedAt: '2026-08-01T00:00:00Z', lastSeenAt: null })],
        page: 1,
        pageSize: 50,
        total: 1,
      },
    })

    renderPage()

    expect(await screen.findByText(/^Banned on /)).toBeTruthy()
    expect(screen.getByText('No activity recorded')).toBeTruthy()
  })

  it('links each row to its own detail route_D179', async () => {
    api.get.mockResolvedValue({
      data: { users: [adminUser()], page: 1, pageSize: 50, total: 1 },
    })

    renderPage()

    const link = await screen.findByRole('link', { name: 'Open Ellen Ripley' })
    expect(link.getAttribute('href')).toBe(
      '/admin/users/0198c2f0-0000-7000-8000-000000000001',
    )
  })

  it('sends the submitted search term to the list endpoint_D179', async () => {
    api.get.mockResolvedValue({
      data: { users: [], page: 1, pageSize: 50, total: 0 },
    })

    renderPage()

    fireEvent.change(await screen.findByLabelText('Search by username or display name'), {
      target: { value: 'ripley' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    expect(await screen.findByText('No account matched that search.')).toBeTruthy()
    expect(api.get).toHaveBeenLastCalledWith('/api/v1/admin/users', {
      params: { query: { query: 'ripley', page: 1 } },
    })
  })
})

describe('AdminUsersPage — D-177 fail-closed parity', () => {
  it('renders a role=alert not-found message on a 404 and names no admin surface_D177', async () => {
    api.get.mockRejectedValue(new TestApiError(404, 'not_found'))

    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toBe('This page is not available.')
    expect(screen.queryByText('User management')).toBeNull()
    expect(document.body.textContent?.toLowerCase()).not.toContain('admin')
  })
})
