import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, PATCH: api.patch, DELETE: api.delete },
  isApiError: () => false,
}))

const mockedUseSession = vi.hoisted(() => vi.fn())

vi.mock('../../auth/useSession', () => ({
  useSession: mockedUseSession,
}))

import '../../i18n'
import { GroupDetailPage } from './GroupDetailPage'

function avatar(emoji: string) {
  return { kind: 'emoji', emoji, avatarSourceId: null, imgUrl: null }
}

function detail() {
  return {
    id: 'g1',
    name: 'D&D crew',
    ownerId: 'owner-1',
    createdAt: '2030-01-01T00:00:00Z',
    members: [
      { userId: 'friend-1', status: 'accepted', invitedAt: '2030-01-01T00:00:00Z', acceptedAt: '2030-01-01T00:00:00Z' },
      { userId: 'friend-2', status: 'invited', invitedAt: '2030-01-01T00:00:00Z', acceptedAt: null },
    ],
  }
}

const FRIENDS = [
  {
    friendshipId: 'f1',
    userId: 'friend-1',
    username: 'friend1',
    displayName: 'Friend One',
    avatar: avatar('🎲'),
    platforms: [],
  },
]

function mockGetGroup() {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/friend-groups/{groupId}') {
      return Promise.resolve({ data: detail() })
    }
    if (path === '/api/v1/friends') {
      return Promise.resolve({ data: { items: FRIENDS } })
    }
    throw new Error(`Unexpected GET ${path}`)
  })
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/groups/g1']}>
        <Routes>
          <Route element={<GroupDetailPage />} path="/groups/:groupId" />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('GroupDetailPage — owner-only management_D199', () => {
  it('gives the owner rename, invite, per-member remove, and delete', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'owner-1', profile: { displayName: 'Me', avatar: avatar('😀') } },
    })
    mockGetGroup()

    renderPage()

    expect(await screen.findByRole('heading', { name: 'D&D crew' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Rename/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Invite friends/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Delete/ })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Remove/ }).length).toBeGreaterThan(0)
    expect(screen.queryByRole('button', { name: /Leave group/ })).not.toBeInTheDocument()
  })

  it('gives a member only a leave action, no owner controls', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'friend-1', profile: { displayName: 'Friend One', avatar: avatar('🎲') } },
    })
    mockGetGroup()

    renderPage()

    expect(await screen.findByRole('heading', { name: 'D&D crew' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Leave group/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Rename/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Invite friends/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Delete/ })).not.toBeInTheDocument()
  })
})
