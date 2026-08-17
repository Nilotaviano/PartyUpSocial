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
import type { FriendGroupDetail } from './groupQueries'

function avatar(emoji: string) {
  return { kind: 'emoji', emoji, avatarSourceId: null, imgUrl: null }
}

function detail(): FriendGroupDetail {
  return {
    id: 'g1',
    name: 'D&D crew',
    ownerId: 'owner-1',
    createdAt: '2030-01-01T00:00:00Z',
    ownerDisplayName: 'Owner One',
    ownerAvatar: avatar('👑'),
    ownerMasked: false,
    members: [
      {
        userId: 'friend-1',
        status: 'accepted',
        invitedAt: '2030-01-01T00:00:00Z',
        acceptedAt: '2030-01-01T00:00:00Z',
        displayName: 'Friend One',
        avatar: avatar('🎲'),
        masked: false,
      },
      {
        userId: 'friend-2',
        status: 'invited',
        invitedAt: '2030-01-01T00:00:00Z',
        acceptedAt: null,
        displayName: 'Friend Two',
        avatar: avatar('🕹️'),
        masked: false,
      },
    ],
  }
}

/**
 * A retained-but-unfriended Accepted member (D-200) alongside a blocked-pair
 * member masked at render (D-076/D-202) — the roster carries real identity
 * data straight from the server for both cases, no client-side friends join.
 */
function detailWithRetainedAndMaskedMembers(): FriendGroupDetail {
  return {
    id: 'g1',
    name: 'D&D crew',
    ownerId: 'owner-1',
    createdAt: '2030-01-01T00:00:00Z',
    ownerDisplayName: 'Owner One',
    ownerAvatar: avatar('👑'),
    ownerMasked: false,
    members: [
      {
        userId: 'retained-1',
        status: 'accepted',
        invitedAt: '2030-01-01T00:00:00Z',
        acceptedAt: '2030-01-01T00:00:00Z',
        displayName: 'Retained Rae',
        avatar: avatar('🎯'),
        masked: false,
      },
      {
        userId: 'blocked-1',
        status: 'accepted',
        invitedAt: '2030-01-01T00:00:00Z',
        acceptedAt: '2030-01-01T00:00:00Z',
        displayName: null,
        avatar: null,
        masked: true,
      },
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

function mockGetGroup(data: FriendGroupDetail = detail()) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/friend-groups/{groupId}') {
      return Promise.resolve({ data })
    }
    if (path === '/api/v1/friends') {
      return Promise.resolve({ data: { items: FRIENDS } })
    }
    throw new Error(`Unexpected GET ${path}`)
  })
}

/** An Invited-only viewer's detail: name + owner id only, no roster (D-199). */
function invitedOnlyDetail(): FriendGroupDetail {
  return {
    id: 'g1',
    name: 'D&D crew',
    ownerId: 'owner-1',
    createdAt: '2030-01-01T00:00:00Z',
    ownerDisplayName: null,
    ownerAvatar: null,
    ownerMasked: false,
    members: null,
  }
}

function mockInvitedOnly() {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/friend-groups/{groupId}') {
      return Promise.resolve({ data: invitedOnlyDetail() })
    }
    if (path === '/api/v1/friend-groups') {
      return Promise.resolve({
        data: {
          owned: [],
          memberOf: [],
          invites: [
            {
              id: 'g1',
              name: 'D&D crew',
              ownerId: 'owner-1',
              invitedAt: '2030-01-01T00:00:00Z',
              ownerDisplayName: 'Owner One',
              ownerAvatar: null,
              ownerMasked: false,
            },
          ],
        },
      })
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

  it("renders the owner's server-resolved name in the eyebrow for a member viewer", async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'friend-1', profile: { displayName: 'Friend One', avatar: avatar('🎲') } },
    })
    mockGetGroup()

    renderPage()

    expect(await screen.findByText('Owned by Owner One')).toBeInTheDocument()
  })
})

describe('GroupDetailPage — server-resolved roster identities, no client-side friends join_D200_D202', () => {
  it('names a retained non-friend Accepted member from the server response_D200', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'owner-1', profile: { displayName: 'Me', avatar: avatar('😀') } },
    })
    mockGetGroup(detailWithRetainedAndMaskedMembers())

    renderPage()

    expect(await screen.findByRole('heading', { name: 'D&D crew' })).toBeInTheDocument()
    // No friends-list entry exists for this id at all, yet the roster still names them —
    // proving the name came from the server response, not a client-side friends join.
    expect(screen.getAllByText('Retained Rae').length).toBeGreaterThan(0)
  })

  it('renders a blocked-pair member as Anonymous User, the row still listed_D076_D202', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'owner-1', profile: { displayName: 'Me', avatar: avatar('😀') } },
    })
    mockGetGroup(detailWithRetainedAndMaskedMembers())

    renderPage()

    expect(await screen.findByRole('heading', { name: 'D&D crew' })).toBeInTheDocument()
    // Renders at least once visibly, plus once more inside the row's sr-only Remove label.
    expect(screen.getAllByText('Anonymous User').length).toBeGreaterThan(0)
    // Still one row per member — masking never drops the row (D-202).
    expect(screen.getAllByRole('button', { name: /^Remove/ }).length).toBe(2)
  })
})

describe('GroupDetailPage — Invited-viewer state_D199', () => {
  it('renders the server-fed name and owner with accept/decline, not the not-found state', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'invitee-1', profile: { displayName: 'Invitee', avatar: avatar('😀') } },
    })
    mockInvitedOnly()

    renderPage()

    expect(await screen.findByRole('heading', { name: 'D&D crew' })).toBeInTheDocument()
    expect(await screen.findByText(/Owner One invited you/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Accept' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Decline' })).toBeInTheDocument()
    expect(screen.queryByText('This group no longer exists')).not.toBeInTheDocument()
  })
})
