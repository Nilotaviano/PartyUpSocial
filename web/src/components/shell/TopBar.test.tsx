import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { TopBar } from './TopBar'

function renderTopBar(initialEntry: string) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/notifications/unread-count') return Promise.resolve({ data: { count: 0 } })
    if (path === '/api/v1/me')
      return Promise.resolve({
        data: { userId: 'u1', username: 'friend.one', displayName: null, avatar: null },
      })
    throw new Error(`Unexpected GET ${path}`)
  })
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <TopBar />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('TopBar', () => {
  it('renders the brand link home and the five section links', async () => {
    renderTopBar('/app')

    expect(await screen.findByRole('link', { name: 'PartyUp home' })).toHaveAttribute('href', '/app')
    expect(screen.getByRole('navigation', { name: 'Primary navigation' })).toBeInTheDocument()
    for (const label of ['Home', 'Friends', 'Sessions', 'Polls', 'Calendar']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument()
    }
  })

  it('marks the current section aria-current="page", including a Sessions sub-route_D139', async () => {
    renderTopBar('/sessions/00000000-0000-0000-0000-000000000001')

    expect(await screen.findByRole('link', { name: 'Sessions' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Home' })).not.toHaveAttribute('aria-current')
  })

  it('renders no theme toggle', async () => {
    renderTopBar('/app')
    await screen.findByRole('link', { name: 'PartyUp home' })

    expect(screen.queryByRole('button', { name: /dark mode|light mode/i })).not.toBeInTheDocument()
  })
})
