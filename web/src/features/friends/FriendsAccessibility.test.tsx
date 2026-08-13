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
import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../api/client', () => ({
  client: { POST: vi.fn(), DELETE: vi.fn() },
  isApiError: () => false,
}))

import '../../i18n'
import { FriendList } from './FriendList'
import { FriendRequests } from './FriendRequests'

function renderComponents() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <div style={{ maxWidth: 320, zoom: 2 }}>
          <FriendList
            items={[
              {
                friendshipId: 'friendship-1',
                userId: 'user-1',
                username: 'friend.one',
                displayName: 'Friend One',
                avatar: {
                  kind: 'emoji',
                  emoji: '🎮',
                  avatarSourceId: null, imgUrl: null,
                },
                platforms: [],
              },
            ]}
            onStatus={() => undefined}
          />
          <FriendRequests
            direction="incoming"
            items={[
              {
                friendshipId: 'request-1',
                username: 'pending.one',
                requestedAt: '2026-08-01T00:00:00Z',
              },
            ]}
            onStatus={() => undefined}
          />
        </div>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function openMenu(trigger: HTMLElement) {
  // Radix's trigger opens on `pointerdown` (mouse), not `click`.
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
}

afterEach(cleanup)

describe('FriendsAccessibilityTests', () => {
  it('KeyboardAndScreenReaderPaths_MeetComponentDuties_D139', async () => {
    renderComponents()

    const profile = screen.getByRole('link', {
      name: /Friend One.*friend\.one/,
    })
    expect(profile).toHaveClass('min-h-11')
    expect(profile.className).toContain('focus-visible:outline-2')

    for (const button of screen.getAllByRole('button')) {
      expect(button).toHaveClass('min-h-11')
      expect(button.className).toContain('focus-visible:outline-2')
    }
    expect(
      screen.getByRole('button', { name: 'Accept request @pending.one' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Reject request @pending.one' }),
    ).toBeInTheDocument()

    // The row's destructive actions live behind its ActionMenu
    // (STYLING_OVERHAUL_DESIGN.md §3.3), so the focus contract now runs
    // trigger → menu → dialog → Escape → back to the trigger.
    const trigger = screen.getByRole('button', {
      name: 'More actions @friend.one',
    })
    trigger.focus()
    openMenu(trigger)
    fireEvent.click(
      await screen.findByRole('menuitem', { name: /Remove friend/ }),
    )

    const dialog = await screen.findByRole('dialog')
    await waitFor(() =>
      expect(
        within(dialog).getByRole('button', { name: 'Cancel' }),
      ).toHaveFocus(),
    )

    fireEvent.keyDown(document, { key: 'Escape' })
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    )
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it('OpenRowMenu_MeetsMenuItemDuties_D139', async () => {
    renderComponents()

    openMenu(
      screen.getByRole('button', { name: 'More actions @friend.one' }),
    )

    const items = await screen.findAllByRole('menuitem')
    expect(items).toHaveLength(2)
    for (const item of items) {
      expect(item).toHaveClass('min-h-11')
      expect(item.className).toMatch(/data-\[highlighted\]:/)
    }
    expect(
      screen.getByRole('menuitem', { name: /Block @friend\.one/ }),
    ).toHaveTextContent('Blocking removes your connection')
  })
})
