import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
}))

const mockedUseSession = vi.hoisted(() => vi.fn())

vi.mock('../../auth/useSession', () => ({
  useSession: mockedUseSession,
}))

import '../../i18n'
import { GroupsPage } from './GroupsPage'

function avatar(emoji: string) {
  return { kind: 'emoji', emoji, avatarSourceId: null, imgUrl: null }
}

const OWNED_GROUP = {
  id: 'g1',
  name: 'D&D crew',
  ownerId: 'owner-1',
  createdAt: '2030-01-01T00:00:00Z',
  memberCount: 2,
}
const MEMBER_GROUP = {
  id: 'g2',
  name: 'FPS squad',
  ownerId: 'friend-1',
  createdAt: '2030-01-02T00:00:00Z',
  memberCount: 2,
}
const INVITE = {
  id: 'g3',
  name: 'Tuesday D&D',
  ownerId: 'friend-2',
  invitedAt: '2030-01-03T00:00:00Z',
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
  {
    friendshipId: 'f2',
    userId: 'friend-2',
    username: 'friend2',
    displayName: 'Friend Two',
    avatar: avatar('🕹️'),
    platforms: [],
  },
]

function groupDetail(groupId: string) {
  if (groupId === 'g1') {
    return {
      id: 'g1',
      name: 'D&D crew',
      ownerId: 'owner-1',
      createdAt: OWNED_GROUP.createdAt,
      members: [{ userId: 'friend-1', status: 'accepted', invitedAt: '2030-01-01T00:00:00Z', acceptedAt: '2030-01-01T00:00:00Z' }],
    }
  }
  return {
    id: 'g2',
    name: 'FPS squad',
    ownerId: 'friend-1',
    createdAt: MEMBER_GROUP.createdAt,
    members: [{ userId: 'owner-1', status: 'accepted', invitedAt: '2030-01-02T00:00:00Z', acceptedAt: '2030-01-02T00:00:00Z' }],
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <GroupsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('GroupsPage', () => {
  it('renders pending invites, my groups, and groups I\'m in from the list query', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'owner-1', profile: { displayName: 'Me', avatar: avatar('😀') } },
    })
    api.get.mockImplementation((path: string, options?: { params?: { path?: { groupId?: string } } }) => {
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({
          data: { owned: [OWNED_GROUP], memberOf: [MEMBER_GROUP], invites: [INVITE] },
        })
      }
      if (path === '/api/v1/friends') {
        return Promise.resolve({ data: { items: FRIENDS } })
      }
      if (path === '/api/v1/friend-groups/{groupId}') {
        return Promise.resolve({ data: groupDetail(options!.params!.path!.groupId!) })
      }
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    expect(await screen.findByText('My groups · 1')).toBeInTheDocument()
    expect(screen.getByText('D&D crew')).toBeInTheDocument()
    expect(screen.getByText("Groups I'm in · 1")).toBeInTheDocument()
    expect(screen.getByText('FPS squad')).toBeInTheDocument()
    expect(screen.getByText('Pending invites · 1')).toBeInTheDocument()
    expect(screen.getByText(/Tuesday D&D/)).toBeInTheDocument()

    // The list card's member count comes straight off FriendGroupSummary.memberCount — both
    // cards render "2 members" with no per-group detail fetch at all (asserted below).
    expect(screen.getAllByText('2 members')).toHaveLength(2)

    // Closes the N+1: listing groups must never fetch any group's own detail endpoint.
    expect(api.get).not.toHaveBeenCalledWith(
      '/api/v1/friend-groups/{groupId}',
      expect.anything(),
    )
  })

  it('fires the accept-invite mutation when Accept is clicked', async () => {
    mockedUseSession.mockReturnValue({
      data: { userId: 'owner-1', profile: { displayName: 'Me', avatar: avatar('😀') } },
    })
    api.get.mockImplementation((path: string, options?: { params?: { path?: { groupId?: string } } }) => {
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({ data: { owned: [], memberOf: [], invites: [INVITE] } })
      }
      if (path === '/api/v1/friends') {
        return Promise.resolve({ data: { items: FRIENDS } })
      }
      if (path === '/api/v1/friend-groups/{groupId}') {
        return Promise.resolve({ data: groupDetail(options!.params!.path!.groupId!) })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.post.mockResolvedValue({ data: { id: 'g3', name: 'Tuesday D&D', ownerId: 'friend-2', createdAt: '2030-01-03T00:00:00Z' } })

    renderPage()

    const invite = await screen.findByText(/Tuesday D&D/)
    const acceptButton = within(invite.closest('li')!).getByRole('button', { name: 'Accept' })
    acceptButton.click()

    await waitFor(() => {
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/friend-groups/{groupId}/invitation/accept',
        expect.objectContaining({ params: expect.objectContaining({ path: { groupId: 'g3' } }) }),
      )
    })
  })
})
