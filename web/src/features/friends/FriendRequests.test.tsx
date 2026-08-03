import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { FriendRequestItem } from './friendGraph'
import '../../i18n'
import { FriendRequests } from './FriendRequests'

function renderRequests(items: FriendRequestItem[]) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <FriendRequests
        direction="incoming"
        items={items}
        onStatus={() => undefined}
      />
    </QueryClientProvider>,
  )
}

afterEach(cleanup)

describe('FriendRequestsTests', () => {
  it('PendingCards_ExposeOnlyUsername_D068', () => {
    const maliciousServerShape = {
      friendshipId: 'friendship-1',
      username: 'pending.one',
      requestedAt: '2026-08-01T00:00:00Z',
      displayName: 'Secret Display Name',
      avatar: {
        kind: 'emoji',
        emoji: 'SECRET-AVATAR',
        sourcePlatformLinkId: null,
      },
      platforms: [{ platform: 'Steam', handle: 'secret-handle' }],
    } as unknown as FriendRequestItem

    renderRequests([maliciousServerShape])

    const card = screen.getByRole('article')
    expect(card).toHaveTextContent('@pending.one')
    expect(card).not.toHaveTextContent('Secret Display Name')
    expect(card).not.toHaveTextContent('SECRET-AVATAR')
    expect(card).not.toHaveTextContent('secret-handle')
    expect(within(card).queryByRole('img')).not.toBeInTheDocument()
    expect(
      within(card).getByRole('button', {
        name: 'Accept request @pending.one',
      }),
    ).toBeInTheDocument()
    expect(
      within(card).getByRole('button', {
        name: 'Reject request @pending.one',
      }),
    ).toBeInTheDocument()
  })
})
