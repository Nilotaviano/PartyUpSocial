import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, DELETE: api.delete },
  isApiError: () => false,
}))

import '../../i18n'
import { FriendsPage } from './FriendsPage'

const friend = {
  friendshipId: 'friendship-accepted',
  userId: 'user-friend-one',
  username: 'friend.one',
  displayName: 'Friend One',
  avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
  platforms: [],
}
let friendVisible = true

const requests = {
  incoming: [
    {
      friendshipId: 'friendship-incoming-1',
      username: 'incoming.one',
      requestedAt: '2026-08-01T00:00:00Z',
    },
    {
      friendshipId: 'friendship-incoming-2',
      username: 'incoming.two',
      requestedAt: '2026-08-01T00:00:00Z',
    },
  ],
  outgoing: [
    {
      friendshipId: 'friendship-outgoing',
      username: 'outgoing.one',
      requestedAt: '2026-08-01T00:00:00Z',
    },
  ],
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <FriendsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  friendVisible = true
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/friends')
      return Promise.resolve({ data: { items: friendVisible ? [friend] : [] } })
    if (path === '/api/v1/friend-requests')
      return Promise.resolve({ data: requests })
    if (path === '/api/v1/friend-share') {
      return Promise.resolve({
        data: {
          friendCode: '23456789AB',
          addUrl: 'https://links.test/add/23456789AB',
          profileUrl: 'https://links.test/u/me',
        },
      })
    }
    throw new Error(`Unexpected GET ${path}`)
  })
  api.post.mockImplementation((path: string) => {
    if (path === '/api/v1/blocks') friendVisible = false
    return Promise.resolve(
      path.endsWith('/accept')
        ? {
            data: {
              friendshipId: 'friendship-incoming-1',
              status: 'accepted',
              direction: 'accepted',
              counterpartUsername: 'incoming.one',
              requestedAt: '2026-08-01T00:00:00Z',
              acceptedAt: '2026-08-02T00:00:00Z',
            },
          }
        : {},
    )
  })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('FriendsPageTests', () => {
  it('RequestActions_RenderAndMutateEveryLifecycleAction_D015', async () => {
    renderPage()

    expect(await screen.findByText('Friend One')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1 })).toHaveFocus()
    expect(
      screen.getByRole('heading', { name: 'Friends (1)' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('heading', { name: 'Incoming requests (2)' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('heading', { name: 'Sent requests (1)' }),
    ).toBeInTheDocument()

    fireEvent.click(
      screen.getByRole('button', { name: 'Accept request @incoming.one' }),
    )
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/friend-requests/{friendshipId}/accept',
        {
          params: {
            header: { 'X-CSRF-TOKEN': '' },
            path: { friendshipId: 'friendship-incoming-1' },
          },
        },
      ),
    )

    fireEvent.click(
      screen.getByRole('button', { name: 'Reject request @incoming.two' }),
    )
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/friend-requests/{friendshipId}/reject',
        {
          params: {
            header: { 'X-CSRF-TOKEN': '' },
            path: { friendshipId: 'friendship-incoming-2' },
          },
        },
      ),
    )

    fireEvent.click(
      screen.getByRole('button', { name: 'Cancel request @outgoing.one' }),
    )
    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/friend-requests/{friendshipId}',
        {
          params: {
            header: { 'X-CSRF-TOKEN': '' },
            path: { friendshipId: 'friendship-outgoing' },
          },
        },
      ),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Block @friend.one' }))
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/blocks', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: 'friend.one' },
      }),
    )
    expect(screen.getByText('User blocked.')).toBeInTheDocument()
    expect(screen.queryByText('Friend One')).not.toBeInTheDocument()
  })

  it('Unfriend_RequiresConfirmationAndShowsNoNotificationCopy_D105', async () => {
    renderPage()

    const trigger = await screen.findByRole('button', {
      name: 'Remove friend @friend.one',
    })
    fireEvent.click(trigger)

    const dialog = screen.getByRole('dialog')
    expect(
      within(dialog).getByText('Shared sessions stay unchanged.', {
        exact: false,
      }),
    ).toBeInTheDocument()
    expect(dialog).not.toHaveTextContent(/notif/i)
    expect(api.delete).not.toHaveBeenCalledWith(
      '/api/v1/friends/{friendshipId}',
      expect.anything(),
    )

    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Remove friend' }),
    )
    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/friends/{friendshipId}',
        {
          params: {
            header: { 'X-CSRF-TOKEN': '' },
            path: { friendshipId: 'friendship-accepted' },
          },
        },
      ),
    )
  })

  it('renders all three empty states and directs the friends state to sharing', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friends')
        return Promise.resolve({ data: { items: [] } })
      if (path === '/api/v1/friend-requests') {
        return Promise.resolve({ data: { incoming: [], outgoing: [] } })
      }
      if (path === '/api/v1/friend-share') {
        return Promise.resolve({
          data: {
            friendCode: '23456789AB',
            addUrl: 'https://links.test/add/23456789AB',
            profileUrl: 'https://links.test/u/me',
          },
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    expect(await screen.findByText('No friends yet.')).toBeInTheDocument()
    expect(screen.getByText('No incoming requests.')).toBeInTheDocument()
    expect(screen.getByText('No sent requests.')).toBeInTheDocument()
    expect(await screen.findByText('23456789AB')).toBeInTheDocument()
  })
})
describe('Friends page error focus', () => {
  it('moves focus to the first mutation error summary_D139', async () => {
    api.post.mockRejectedValueOnce(new Error('failed'))
    renderPage()

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Accept request @incoming.one',
      }),
    )

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveFocus()
  })
})
