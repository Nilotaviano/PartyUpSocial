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
import { ToastDispatchContext } from '../../components/ui/toastStore'
import { FriendList } from './FriendList'

const FRIEND = {
  friendshipId: 'friendship-1',
  userId: 'friend-1',
  username: 'friend.one',
  displayName: 'Friend One',
  avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
  platforms: [],
}

function renderList(toastSpy?: (toast: { message: string }) => void) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastDispatchContext.Provider value={toastSpy ?? (() => {})}>
        <MemoryRouter>
          <FriendList items={[FRIEND]} onStatus={() => {}} />
        </MemoryRouter>
      </ToastDispatchContext.Provider>
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
  const CONTAINING = '/api/v1/friend-groups/containing/{userId}'

  function mockContaining(groups: { id: string; name: string }[]) {
    api.get.mockImplementation((path: string, options?: unknown) => {
      if (path === CONTAINING) {
        return Promise.resolve({
          data: {
            groups: groups.map((group) => ({
              ...group,
              ownerId: 'me',
              createdAt: '2030-01-01T00:00:00Z',
              memberCount: 2,
              ownerMasked: false,
            })),
          },
        })
      }
      throw new Error(`Unexpected GET ${path} ${JSON.stringify(options)}`)
    })
  }

  it('resolves the remains-in groups with a single request, and fires member-remove after unfriend when checked_D210', async () => {
    mockContaining([{ id: 'g1', name: 'D&D crew' }])
    api.delete.mockResolvedValue({})

    renderList()
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Will remain in: D&D crew')).toBeInTheDocument()

    // One round trip for the whole answer — no per-owned-group roster fan-out.
    expect(api.get).toHaveBeenCalledTimes(1)
    expect(api.get).toHaveBeenCalledWith(
      CONTAINING,
      expect.objectContaining({ params: { path: { userId: 'friend-1' } } }),
    )

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
    mockContaining([{ id: 'g1', name: 'D&D crew' }])
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

  it('names the groups whose removal failed and leaves the succeeded ones unnamed_D210', async () => {
    mockContaining([
      { id: 'g1', name: 'D&D crew' },
      { id: 'g2', name: 'Raid night' },
    ])
    api.delete.mockImplementation((path: string, options: { params: { path: { groupId?: string } } }) => {
      if (path === '/api/v1/friend-groups/{groupId}/members/{userId}' && options.params.path.groupId === 'g2') {
        return Promise.reject(new Error('boom'))
      }
      return Promise.resolve({})
    })
    const toastSpy = vi.fn()

    renderList(toastSpy)
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Will remain in: D&D crew, Raid night')).toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove friend' }))

    // The unfriend itself succeeded, so the dialog closes regardless of the
    // group-removal outcome that follows it — but the failure is reported, and
    // reported per group: only the one still holding them is named.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() =>
      expect(toastSpy).toHaveBeenCalledWith({
        message: "Couldn't remove them from: Raid night. Try again from the group page.",
      }),
    )
  })

  it('renders no remains-in note when the target is in none of the actor owned groups', async () => {
    mockContaining([])
    api.delete.mockResolvedValue({})

    renderList()
    openUnfriendDialog()

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByText(/Will remain in/)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument()
  })
})
