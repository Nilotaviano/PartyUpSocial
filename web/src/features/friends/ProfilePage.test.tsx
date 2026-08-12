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
import { ProfilePage } from './ProfilePage'

function renderProfile(username = 'friend.one') {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={['/u/' + username]}>
        <Routes>
          <Route path="/u/:username" element={<ProfilePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('ProfilePage', () => {
  it('renders only username and relationship controls before acceptance_D068', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
          displayName: 'Secret Display Name',
          avatar: {
            kind: 'emoji',
            emoji: 'secret-avatar',
            avatarSourceId: null, imgUrl: null,
          },
          platforms: [{ platform: 'Steam', handle: 'secret-handle' }],
        },
      },
    })

    renderProfile()

    expect(await screen.findByText('@friend.one')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1 })).toHaveFocus()
    expect(screen.queryByText('Secret Display Name')).not.toBeInTheDocument()
    expect(screen.queryByText('secret-avatar')).not.toBeInTheDocument()
    expect(screen.queryByText(/secret-handle/)).not.toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Send friend request' }),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('renders full identity only for a full disclosure_D032', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'full',
          username: 'friend.one',
          displayName: 'Friend One',
          avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
          platforms: [{ platform: 'Steam', handle: 'friendly' }],
          relationship: { kind: 'accepted', friendshipId: 'friendship-1' },
        },
      },
    })

    renderProfile()

    expect(
      await screen.findByRole('heading', { name: 'Friend One' }),
    ).toBeInTheDocument()
    expect(screen.getByText('@friend.one')).toBeInTheDocument()
    expect(screen.getByText('Steam: friendly')).toBeInTheDocument()
  })

  it('sends a request only after the explicit action and refetches the canonical profile_D015', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
        },
      },
    })
    api.post.mockResolvedValue({ data: { friendshipId: 'friendship-1' } })

    renderProfile('Friend.One')

    const button = await screen.findByRole('button', {
      name: 'Send friend request',
    })
    expect(api.post).not.toHaveBeenCalled()
    fireEvent.click(button)

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/friend-requests', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: 'friend.one' },
      }),
    )
    await waitFor(() => expect(api.get.mock.calls.length).toBeGreaterThan(1))
    expect(await screen.findByText('Friend request sent.')).toBeInTheDocument()
  })

  it('reconciles a block across profile and list query families_D065', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
        },
      },
    })
    api.post.mockResolvedValue({ data: { blockId: 'block-1' } })

    renderProfile()
    fireEvent.click(await screen.findByRole('button', { name: 'Block' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/blocks', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: 'friend.one' },
      }),
    )
    expect(await screen.findByText('User blocked.')).toBeInTheDocument()
  })
})
