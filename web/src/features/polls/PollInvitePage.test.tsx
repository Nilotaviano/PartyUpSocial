import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
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
import { PollInvitePage } from './PollInvitePage'

function pollDetail() {
  return {
    id: 'poll-1',
    organizerId: 'organizer-1',
    title: 'Raid night poll',
    platform: 'notApplicable',
    game: null,
    notes: '',
    minAttendance: 4,
    maxCapacity: 4,
    isPrivate: false,
    deadlineChoice: 'hours24',
    deadline: '2099-01-02T00:00:00Z',
    selectionWindowEndsAt: '2099-01-04T00:00:00Z',
    status: 'polling',
    createdAt: '2099-01-01T00:00:00Z',
    selectedOptionId: null,
    createdSessionId: null,
    options: [],
    roster: [],
    muted: false,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/polls/poll-1/invite']}>
        <Routes>
          <Route path="/polls/:pollId/invite" element={<PollInvitePage />} />
          <Route path="/polls/:pollId" element={<div data-testid="navigated-to-poll" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/polls/{pollId}') {
      return Promise.resolve({ data: pollDetail() })
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
  api.post.mockResolvedValue({ data: pollDetail() })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollInvitePage', () => {
  it('invites the selected friends and navigates back to the poll_D193', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Invite friends' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls/{pollId}/invites',
        expect.objectContaining({ body: { userIds: ['user-1'] } }),
      ),
    )
    expect(await screen.findByTestId('navigated-to-poll')).toBeInTheDocument()
  })

  it('shows the friend avatar without changing the checkbox accessible name, and toggles by click and keyboard_D193', async () => {
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

  it('the invite button is disabled with no friends selected_D193', async () => {
    renderPage()

    await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    expect(screen.getByRole('button', { name: 'Invite friends' })).toBeDisabled()
  })

  it('distinguishes loading, failed, and empty friends-checklist states (Wave 6)', async () => {
    let resolveFriends: (() => void) | undefined
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/polls/{pollId}') return Promise.resolve({ data: pollDetail() })
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

  it('surfaces a friends-list load failure distinctly from an empty list (Wave 6)', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/polls/{pollId}') return Promise.resolve({ data: pollDetail() })
      if (path === '/api/v1/friends') return Promise.reject(new ApiError(500, 'unknown'))
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText("Couldn't load your friends list.")).toBeInTheDocument()
    expect(screen.queryByText('You have no friends to invite yet.')).not.toBeInTheDocument()
  })

  it('shows a single uniform failure message for an unusable invitee_D193', async () => {
    api.post.mockRejectedValue(new ApiError(404, 'user_not_found'))
    renderPage()

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Invite friends' }))

    expect(await screen.findByText('That user could not be found.')).toBeInTheDocument()
  })
})
