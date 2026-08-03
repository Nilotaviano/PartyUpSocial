import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

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
import { AddFriendPage } from './AddFriendPage'

function renderAdd(path: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries')
  return {
    queryClient,
    invalidateQueries,
    ...render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route path="/add/:code" element={<AddFriendPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    ),
  }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AddFriendPage', () => {
  it('normalizes spaces, dashes, and case before resolving_D144', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
        },
      },
    })

    renderAdd('/add/2345-6789-ab')

    expect(await screen.findByText('@friend.one')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1 })).toHaveFocus()
    expect(screen.getByLabelText('Friend code')).toHaveValue('23456789AB')
    expect(api.get).toHaveBeenCalledWith('/api/v1/friend-codes/{friendCode}', {
      params: { path: { friendCode: '23456789AB' } },
    })
    expect(api.post).not.toHaveBeenCalled()
  })

  it('keeps the entered code after a failed resolution_D144', async () => {
    api.get.mockRejectedValue(new Error('missing'))
    renderAdd('/add/23456789AB')

    await screen.findByRole('alert')
    expect(screen.getByLabelText('Friend code')).toHaveValue('23456789AB')
  })

  it('rejects invalid input locally without making a lookup_D144', async () => {
    renderAdd('/add/invalid')

    const input = screen.getByLabelText('Friend code')
    fireEvent.change(input, { target: { value: '2345 6789AB' } })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Find friend' }).closest('form')!,
    )

    const alert = screen.getAllByRole('alert')[0]
    expect(alert).toBeInTheDocument()
    await waitFor(() => expect(alert).toHaveFocus())
    expect(api.get).not.toHaveBeenCalled()
  })
})
describe('AddFriendPageTests Slice 8 actions', () => {
  it('ResolvedProfile_AllowsAnExplicitFriendRequest_D031', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
        },
      },
    })
    api.post.mockResolvedValue({
      data: {
        friendshipId: 'friendship-1',
        status: 'pending',
        direction: 'outgoing',
        counterpartUsername: 'friend.one',
        requestedAt: '2026-08-01T00:00:00Z',
      },
    })

    renderAdd('/add/2345-6789-ab')
    expect(api.post).not.toHaveBeenCalled()
    fireEvent.click(
      await screen.findByRole('button', { name: 'Send friend request' }),
    )

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/friend-requests', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: 'friend.one' },
      }),
    )
  })

  it('SendAndBlock_InvalidateCanonicalCodeAndReconcileActionCard_D031', async () => {
    let relationship: 'none' | 'outgoing_pending' = 'none'
    api.get.mockImplementation(() =>
      Promise.resolve({
        data: {
          profile: {
            disclosure: 'username_only',
            username: 'friend.one',
            relationship:
              relationship === 'none'
                ? { kind: 'none' }
                : { kind: 'outgoing_pending', friendshipId: 'friendship-1' },
          },
        },
      }),
    )
    api.post.mockImplementation((path: string) => {
      if (path === '/api/v1/friend-requests') {
        relationship = 'outgoing_pending'
        return Promise.resolve({
          data: {
            friendshipId: 'friendship-1',
            status: 'pending',
            direction: 'outgoing',
            counterpartUsername: 'friend.one',
            requestedAt: '2026-08-01T00:00:00Z',
          },
        })
      }
      return Promise.resolve({ data: { blockId: 'block-1' } })
    })

    const { invalidateQueries } = renderAdd('/add/2345-6789-ab')
    fireEvent.click(
      await screen.findByRole('button', { name: 'Send friend request' }),
    )

    expect(
      await screen.findByRole('button', { name: 'Cancel request' }),
    ).toBeInTheDocument()
    expect(invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['friendCode', '23456789AB'],
      exact: true,
    })

    fireEvent.click(screen.getByRole('button', { name: 'Block' }))
    expect(
      await screen.findByText('This profile could not be found.'),
    ).toBeInTheDocument()
    expect(screen.queryByText('@friend.one')).not.toBeInTheDocument()
    expect(invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['blocks'],
      exact: true,
    })
  })
})
