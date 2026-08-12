import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SteamImportResult } from './friendGraph'

const api = vi.hoisted(() => ({ post: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post, DELETE: api.delete },
  isApiError: () => false,
}))

import '../../i18n'
import { SteamImportPage } from './SteamImportPage'

function renderPage() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <SteamImportPage />
    </QueryClientProvider>,
  )
}

function result(state: SteamImportResult['state']): SteamImportResult {
  const invitationUrl = 'https://links.test/add/23456789AB'
  switch (state) {
    case 'ready':
      return {
        state,
        matches: [
          {
            userId: '00000000-0000-0000-0000-0000000000a1',
            username: 'ready.one',
            avatar: { kind: 'emoji', emoji: '🙂', avatarSourceId: null, imgUrl: null },
          },
        ],
        unmatched: [],
        invitationUrl,
      }
    case 'private_profile':
      return { state, matches: [], unmatched: [], invitationUrl }
    case 'zero_friends':
      return { state, matches: [], unmatched: [], invitationUrl }
    case 'zero_matches':
      return { state, matches: [], unmatched: [], invitationUrl }
    case 'not_configured':
      return { state, invitationUrl }
    case 'upstream_failure':
      return { state, retryable: true, invitationUrl }
  }
}

beforeEach(() => {
  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    value: { writeText: vi.fn().mockResolvedValue(undefined) },
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SteamImportPageTests', () => {
  it.each([
    ['ready', 'Steam friends found'],
    ['private_profile', 'Your Steam friends list is private'],
    ['zero_friends', 'No Steam friends found'],
    ['zero_matches', 'No PartyUp matches yet'],
    ['not_configured', 'Steam import is unavailable'],
    ['upstream_failure', 'Steam could not be reached'],
  ] as const)(
    'Render_DistinguishesEveryProviderAndEmptyState_D049: %s',
    async (state, heading) => {
      api.post.mockResolvedValueOnce({ data: result(state) })
      renderPage()

      expect(api.post).not.toHaveBeenCalled()
      fireEvent.click(
        screen.getByRole('button', { name: 'Find Steam friends' }),
      )

      expect(
        await screen.findByRole('heading', { name: heading }),
      ).toBeInTheDocument()
      if (state === 'private_profile') {
        const help = screen.getByRole('link', {
          name: 'Open Steam privacy settings in a new tab',
        })
        expect(help).toHaveAttribute('target', '_blank')
      }
      if (state === 'upstream_failure') {
        expect(
          screen.getByRole('button', { name: 'Try again' }),
        ).toBeInTheDocument()
      }
    },
  )

  it('HiddenLocalCandidate_IsNeverLabeledAsRegistered_D065', async () => {
    api.post.mockResolvedValueOnce({
      data: {
        state: 'zero_matches',
        matches: [],
        unmatched: [
          {
            steamId64: '76561198000000001',
            personaName: 'Steam Pal',
            avatarUrl: 'https://cdn.test/avatar.png',
            invitationUrl: 'https://links.test/add/23456789AB',
          },
        ],
        invitationUrl: 'https://links.test/add/23456789AB',
      },
    })
    renderPage()

    fireEvent.click(screen.getByRole('button', { name: 'Find Steam friends' }))

    expect(await screen.findByText('Steam Pal')).toBeInTheDocument()
    expect(
      screen.getByText('Steam friend not yet matched to PartyUp'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/PartyUp account:/)).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: /Send friend request/ }),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: /^Block/ }),
    ).not.toBeInTheDocument()
    expect(
      screen.getAllByRole('button', { name: /Copy invitation link/ }).length,
    ).toBeGreaterThan(0)
  })

  it('SendAndBlock_ReconcileOrRemoveMatchingActionCards_D065', async () => {
    const imported: SteamImportResult = {
      state: 'ready',
      matches: [
        {
          userId: '00000000-0000-0000-0000-0000000000a2',
          username: 'player.one',
          avatar: { kind: 'emoji', emoji: '🙂', avatarSourceId: null, imgUrl: null },
        },
        {
          userId: '00000000-0000-0000-0000-0000000000a3',
          username: 'player.one',
          avatar: { kind: 'emoji', emoji: '🙂', avatarSourceId: null, imgUrl: null },
        },
        {
          userId: '00000000-0000-0000-0000-0000000000a4',
          username: 'block.one',
          avatar: { kind: 'emoji', emoji: '🙂', avatarSourceId: null, imgUrl: null },
        },
      ],
      unmatched: [],
      invitationUrl: 'https://links.test/add/23456789AB',
    }
    api.post.mockImplementation((path: string) => {
      if (path === '/api/v1/friends/imports/steam') {
        return Promise.resolve({ data: imported })
      }
      if (path === '/api/v1/friend-requests') {
        return Promise.resolve({
          data: {
            friendshipId: 'friendship-1',
            status: 'pending',
            direction: 'outgoing',
            counterpartUsername: 'player.one',
            requestedAt: '2026-08-01T00:00:00Z',
          },
        })
      }
      if (path === '/api/v1/blocks') {
        return Promise.resolve({ data: { blockId: 'block-1' } })
      }
      throw new Error(`Unexpected POST ${path}`)
    })
    renderPage()

    fireEvent.click(screen.getByRole('button', { name: 'Find Steam friends' }))
    const sendButtons = await screen.findAllByRole('button', {
      name: 'Send friend request @player.one',
    })
    expect(sendButtons).toHaveLength(2)
    fireEvent.click(sendButtons[0])

    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: 'Send friend request @player.one',
        }),
      ).not.toBeInTheDocument(),
    )
    expect(screen.getAllByText('Request sent')).toHaveLength(2)

    fireEvent.click(screen.getByRole('button', { name: 'Block @block.one' }))
    await waitFor(() =>
      expect(screen.queryByText('@block.one')).not.toBeInTheDocument(),
    )
    expect(api.post).toHaveBeenCalledWith('/api/v1/blocks', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { targetUsername: 'block.one' },
    })
  })

  it('a matched card carries the avatar disc and the heading name excludes it_D188', async () => {
    api.post.mockResolvedValueOnce({
      data: {
        state: 'ready',
        matches: [
          {
            userId: '00000000-0000-0000-0000-0000000000a5',
            username: 'ready.one',
            avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
          },
        ],
        unmatched: [],
        invitationUrl: 'https://links.test/add/23456789AB',
      },
    })
    renderPage()

    fireEvent.click(screen.getByRole('button', { name: 'Find Steam friends' }))

    const heading = await screen.findByRole('heading', { name: '@ready.one' })
    const card = heading.closest('article') as HTMLElement
    expect(card.querySelector('[aria-hidden="true"]')).toHaveTextContent('🎮')
    expect(heading).not.toHaveTextContent('🎮')
  })
})
