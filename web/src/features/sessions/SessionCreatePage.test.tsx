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
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

const navigate = vi.hoisted(() => vi.fn())
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigate }
})

import '../../i18n'
import { SessionCreatePage } from './SessionCreatePage'

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <SessionCreatePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockResolvedValue({ data: { items: [] } })
  api.post.mockResolvedValue({
    data: {
      session: { id: 'session-1' },
      guestLinks: [],
    },
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionCreatePage', () => {
  it('shows the fixed-session defaults_D129', async () => {
    renderPage()

    expect(await screen.findByText(/4 minimum, 4 maximum, 2 hours/i)).toBeInTheDocument()
    expect(screen.getByLabelText('Minimum attendance')).toHaveValue(4)
    expect(screen.getByLabelText('Maximum capacity')).toHaveValue(4)
    expect(screen.getByLabelText('Duration')).toHaveValue(120)
  })

  it('rejects a duration outside the 15-minute increment and blocks submission_D129', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), {
      target: { value: 'Raid night' },
    })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.change(screen.getByLabelText('Duration'), {
      target: { value: '10' },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('requires an invited friend for a non-private session and creates one_D129', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          {
            friendshipId: 'friendship-1',
            userId: 'user-1',
            username: 'friend.one',
            displayName: 'Friend One',
            avatar: { kind: 'emoji', emoji: '🎮', sourcePlatformLinkId: null },
            platforms: [],
          },
        ],
      },
    })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), {
      target: { value: 'Raid night' },
    })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      (await screen.findAllByText('A non-private session needs at least one invited friend.')).length,
    ).toBeGreaterThan(0)

    fireEvent.click(
      await screen.findByLabelText('Friend One (@friend.one)'),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({ body: expect.objectContaining({ inviteeUserIds: ['user-1'] }) }),
      ),
    )
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/sessions/session-1'))
  })

  it('warns before discarding dirty create-form values_D152_D162', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), {
      target: { value: 'Draft title' },
    })

    const cancelTrigger = screen.getByRole('button', { name: 'Cancel' })
    cancelTrigger.focus()
    fireEvent.click(cancelTrigger)

    let dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Discard unsaved changes?')
    await waitFor(() =>
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus(),
    )

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('Title')).toHaveValue('Draft title')
    expect(cancelTrigger).toHaveFocus()
    expect(navigate).not.toHaveBeenCalled()

    fireEvent.click(cancelTrigger)
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    expect(navigate).toHaveBeenCalledWith('/sessions/upcoming')
  })
})
