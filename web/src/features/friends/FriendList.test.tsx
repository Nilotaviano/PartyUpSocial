import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, DELETE: api.delete },
  isApiError: () => false,
}))

import '../../i18n'
import { FriendList } from './FriendList'

const FRIEND = {
  friendshipId: 'friendship-1',
  userId: 'friend-1',
  username: 'friend.one',
  displayName: 'Friend One',
  avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
  platforms: [],
}

function renderList() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <FriendList items={[FRIEND]} onStatus={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function openUnfriendDialog() {
  fireEvent.pointerDown(screen.getByRole('button', { name: /More actions/ }), {
    button: 0,
    pointerType: 'mouse',
  })
  fireEvent.click(screen.getAllByRole('menuitem', { name: /Remove friend/ })[0])
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('UnfriendDialog — D210 shared-group surfacing', () => {
  it('lists the owned groups the target remains in, and fires member-remove after unfriend when checked', async () => {
    api.get.mockImplementation((path: string, options?: { params?: { path?: { groupId?: string } } }) => {
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({
          data: {
            owned: [{ id: 'g1', name: 'D&D crew', ownerId: 'me', createdAt: '2030-01-01T00:00:00Z', memberCount: 2, ownerMasked: false }],
            memberOf: [],
            invites: [],
          },
        })
      }
      if (path === '/api/v1/friend-groups/{groupId}') {
        return Promise.resolve({
          data: {
            id: 'g1',
            name: 'D&D crew',
            ownerId: 'me',
            createdAt: '2030-01-01T00:00:00Z',
            ownerMasked: false,
            members: [
              { userId: 'friend-1', status: 'accepted', invitedAt: '2030-01-01T00:00:00Z', acceptedAt: '2030-01-01T00:00:00Z' },
            ],
          },
        })
      }
      throw new Error(`Unexpected GET ${path} ${JSON.stringify(options)}`)
    })
    api.delete.mockResolvedValue({})

    renderList()
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Will remain in: D&D crew')).toBeInTheDocument()
    const checkbox = within(dialog).getByRole('checkbox', { name: 'Also remove from my groups' })
    // Checked by default, per the Lovable canon.
    expect(checkbox).toBeChecked()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove friend' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/friends/{friendshipId}',
        expect.objectContaining({ params: expect.objectContaining({ path: { friendshipId: 'friendship-1' } }) }),
      ),
    )
    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/friend-groups/{groupId}/members/{userId}',
        expect.objectContaining({
          params: expect.objectContaining({ path: { groupId: 'g1', userId: 'friend-1' } }),
        }),
      ),
    )
  })

  it('skips every group-removal mutation when the checkbox is unchecked_D210', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({
          data: {
            owned: [{ id: 'g1', name: 'D&D crew', ownerId: 'me', createdAt: '2030-01-01T00:00:00Z', memberCount: 2, ownerMasked: false }],
            memberOf: [],
            invites: [],
          },
        })
      }
      if (path === '/api/v1/friend-groups/{groupId}') {
        return Promise.resolve({
          data: {
            id: 'g1',
            name: 'D&D crew',
            ownerId: 'me',
            createdAt: '2030-01-01T00:00:00Z',
            ownerMasked: false,
            members: [
              { userId: 'friend-1', status: 'accepted', invitedAt: '2030-01-01T00:00:00Z', acceptedAt: '2030-01-01T00:00:00Z' },
            ],
          },
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.delete.mockResolvedValue({})

    renderList()
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    const checkbox = await within(dialog).findByRole('checkbox', { name: 'Also remove from my groups' })
    fireEvent.click(checkbox)
    expect(checkbox).not.toBeChecked()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove friend' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/friends/{friendshipId}',
        expect.anything(),
      ),
    )
    expect(api.delete).not.toHaveBeenCalledWith(
      '/api/v1/friend-groups/{groupId}/members/{userId}',
      expect.anything(),
    )
  })

  it('renders no remains-in note when the target is in none of the actor owned groups', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({ data: { owned: [], memberOf: [], invites: [] } })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.delete.mockResolvedValue({})

    renderList()
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByText(/Will remain in/)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument()
  })
})
