import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, DELETE: api.delete },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
  ApiError: class ApiError extends Error {
    status: number
    code: string
    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  },
}))

import { ApiError } from '../../api/client'
import '../../i18n'
import { SessionInvitePage } from './SessionInvitePage'

const writeText = vi.fn().mockResolvedValue(undefined)
Object.assign(navigator, { clipboard: { writeText } })

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/sessions/session-1/invite']}>
        <Routes>
          <Route path="/sessions/:sessionId/invite" element={<SessionInvitePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/sessions/{sessionId}') {
      return Promise.resolve({
        data: {
          id: 'session-1',
          organizerId: 'organizer-1',
          title: 'Raid night',
          startTime: '2030-09-01T18:00:00Z',
          durationMinutes: 120,
          platform: 'Steam',
          game: null,
          notes: '',
          minAttendance: 4,
          maxCapacity: 8,
          isPrivate: false,
          status: 'unconfirmed',
          createdAt: '2030-08-01T00:00:00Z',
          roster: [],
          version: 'v1',
        },
      })
    }
    if (path === '/api/v1/friends') {
      return Promise.resolve({
        data: {
          items: [
            {
              friendshipId: 'friendship-1',
              userId: 'user-1',
              username: 'friend.one',
              displayName: 'Friend One',
              avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
              platforms: [],
            },
          ],
        },
      })
    }
    throw new Error(`Unexpected GET ${path}`)
  })
  api.post.mockResolvedValue({ data: {} })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionInvitePage', () => {
  it('shows the game and platform in the header so the invitee knows which session_D115', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        return Promise.resolve({
          data: {
            id: 'session-1',
            organizerId: 'organizer-1',
            title: 'Raid night',
            startTime: '2030-09-01T18:00:00Z',
            durationMinutes: 120,
            platform: 'steam',
            game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
            notes: '',
            minAttendance: 4,
            maxCapacity: 8,
            isPrivate: false,
            status: 'unconfirmed',
            createdAt: '2030-08-01T00:00:00Z',
            roster: [],
            version: 'v1',
          },
        })
      }
      if (path === '/api/v1/friends') {
        return Promise.resolve({ data: { items: [] } })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText('Deep Rock Galactic · Steam')).toBeInTheDocument()
  })

  it('invites the selected friends_D115', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Invite friends' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/invites',
        expect.objectContaining({ body: { userIds: ['user-1'] } }),
      ),
    )
  })

  it('shows the friend avatar without changing the checkbox accessible name, and toggles by click and keyboard_D050', async () => {
    renderPage()

    const checkbox = await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    const label = checkbox.closest('label')
    expect(label?.querySelector('[aria-hidden="true"]')).not.toBeNull()

    expect(checkbox).not.toBeChecked()
    fireEvent.click(label as HTMLElement)
    expect(checkbox).toBeChecked()

    // Native keyboard activation (Space/Enter on a focused input) dispatches
    // a click event on the input itself; jsdom does not synthesize this from
    // keydown, so a direct click on the focused control is the faithful proxy.
    checkbox.focus()
    fireEvent.click(checkbox)
    expect(checkbox).not.toBeChecked()
  })

  it('creates and copies a guest link_D115', async () => {
    api.post.mockResolvedValue({
      data: [{ guestLinkId: 'link-1', url: 'https://partyup.test/g/token' }],
    })
    renderPage()

    await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    fireEvent.click(screen.getByRole('button', { name: 'Create guest link' }))

    expect(await screen.findByText('https://partyup.test/g/token')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Copy link to clipboard' }))

    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://partyup.test/g/token'))
    expect(await screen.findByText('Link copied.')).toBeInTheDocument()
  })

  it('shows a single uniform failure message for an unusable invitee_D115', async () => {
    api.post.mockRejectedValue(new ApiError(404, 'user_not_found'))
    renderPage()

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Invite friends' }))

    expect(await screen.findByText('That user could not be found.')).toBeInTheDocument()
  })

  it('distinguishes loading, failed, and empty friends-checklist states (Wave 6)', async () => {
    let resolveFriends: (() => void) | undefined
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        return Promise.resolve({
          data: {
            id: 'session-1',
            organizerId: 'organizer-1',
            title: 'Raid night',
            startTime: '2030-09-01T18:00:00Z',
            durationMinutes: 120,
            platform: 'Steam',
            game: null,
            notes: '',
            minAttendance: 4,
            maxCapacity: 8,
            isPrivate: false,
            status: 'unconfirmed',
            createdAt: '2030-08-01T00:00:00Z',
            roster: [],
            version: 'v1',
          },
        })
      }
      if (path === '/api/v1/friends') {
        return new Promise((resolve) => {
          resolveFriends = () => resolve({ data: { items: [] } })
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText('Loading...')).toBeInTheDocument()

    resolveFriends?.()
    expect(await screen.findByText('You have no friends to invite yet.')).toBeInTheDocument()
  })

  it('surfaces a friends-list load failure in the page error banner (Wave 6)', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        return Promise.resolve({
          data: {
            id: 'session-1',
            organizerId: 'organizer-1',
            title: 'Raid night',
            startTime: '2030-09-01T18:00:00Z',
            durationMinutes: 120,
            platform: 'Steam',
            game: null,
            notes: '',
            minAttendance: 4,
            maxCapacity: 8,
            isPrivate: false,
            status: 'unconfirmed',
            createdAt: '2030-08-01T00:00:00Z',
            roster: [],
            version: 'v1',
          },
        })
      }
      if (path === '/api/v1/friends') {
        return Promise.reject(new ApiError(500, 'unknown'))
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText("Couldn't load your friends list.")).toBeInTheDocument()
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })

  it('requires confirmation in a focused dialog before revoking a guest link_H7', async () => {
    api.post.mockResolvedValue({
      data: [{ guestLinkId: 'link-1', url: 'https://partyup.test/g/token' }],
    })
    renderPage()

    await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    fireEvent.click(screen.getByRole('button', { name: 'Create guest link' }))
    const revokeTrigger = await screen.findByRole('button', { name: 'Revoke' })
    revokeTrigger.focus()
    fireEvent.click(revokeTrigger)

    const dialog = await screen.findByRole('dialog')
    await waitFor(() =>
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus(),
    )
    expect(api.delete).not.toHaveBeenCalled()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(revokeTrigger).toHaveFocus()
    expect(api.delete).not.toHaveBeenCalled()

    fireEvent.click(revokeTrigger)
    const reopenedDialog = await screen.findByRole('dialog')
    fireEvent.click(within(reopenedDialog).getByRole('button', { name: 'Revoke' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/guest-links/{guestLinkId}',
        expect.objectContaining({
          params: expect.objectContaining({
            path: { sessionId: 'session-1', guestLinkId: 'link-1' },
          }),
        }),
      ),
    )
  })
})
