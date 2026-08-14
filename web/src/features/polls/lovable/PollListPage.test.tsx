import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../../i18n'
import { PollListPage } from './PollListPage'

function poll(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'poll-1',
    organizerId: 'organizer-1',
    title: 'Raid night poll',
    status: 'polling',
    isPrivate: false,
    deadline: '2099-01-02T00:00:00Z',
    selectionWindowEndsAt: '2099-01-04T00:00:00Z',
    createdAt: '2099-01-01T00:00:00Z',
    optionCount: 3,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <PollListPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollListPage', () => {
  it('renders the involved band by default from GET /polls', async () => {
    api.get.mockImplementation(
      (_path: string, options?: { params: { query: { band?: string } } }) => {
        const band = options?.params.query.band
        if (band === 'discoverable') {
          return Promise.resolve({
            data: { items: [poll({ id: 'poll-2', title: 'Open lobby' })], nextCursor: null },
          })
        }
        return Promise.resolve({
          data: { items: [poll({ title: 'Raid night poll' })], nextCursor: null },
        })
      },
    )
    renderPage()

    expect(await screen.findByText('Raid night poll')).toBeInTheDocument()
    expect(screen.queryByText('Open lobby')).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Your polls' })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    expect(screen.getByRole('tab', { name: 'Discover' })).toHaveAttribute(
      'aria-selected',
      'false',
    )
  })

  it('switches the query family when the discoverable tab is activated', async () => {
    api.get.mockImplementation(
      (_path: string, options?: { params: { query: { band?: string } } }) => {
        const band = options?.params.query.band
        if (band === 'discoverable') {
          return Promise.resolve({
            data: { items: [poll({ id: 'poll-2', title: 'Open lobby' })], nextCursor: null },
          })
        }
        return Promise.resolve({
          data: { items: [poll({ title: 'Raid night poll' })], nextCursor: null },
        })
      },
    )
    renderPage()

    expect(await screen.findByText('Raid night poll')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: 'Discover' }))

    expect(await screen.findByText('Open lobby')).toBeInTheDocument()
    expect(screen.queryByText('Raid night poll')).not.toBeInTheDocument()
    expect(
      api.get.mock.calls.some(
        (call) => (call[1] as { params: { query: { band?: string } } }).params.query.band ===
          'discoverable',
      ),
    ).toBe(true)
  })

  it('shows a status pill with the deadline as text while polling', async () => {
    const deadline = '2099-06-15T23:00:00Z'
    api.get.mockResolvedValue({ data: { items: [poll({ deadline })], nextCursor: null } })
    renderPage()

    const expected = new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(deadline))
    expect(await screen.findByText(`Polling · closes ${expected}`)).toBeInTheDocument()
  })

  it('shows closed-awaiting-selection once the deadline has passed, even if status is still polling', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [poll({ deadline: new Date(Date.now() - 60_000).toISOString() })],
        nextCursor: null,
      },
    })
    renderPage()

    expect(await screen.findByText('Closed · awaiting selection')).toBeInTheDocument()
  })

  it('shows the involved empty state with a create-poll way out', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage()

    expect(await screen.findByText('You have no active polls.')).toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: 'Create a poll' }),
    ).toHaveAttribute('href', '/polls/new')
  })

  it('shows the discoverable empty state with no CTA', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage()

    fireEvent.click(screen.getByRole('tab', { name: 'Discover' }))

    expect(
      await screen.findByText('No open polls to discover right now.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Create a poll' })).not.toBeInTheDocument()
  })

  it('loads the next cursor page for the active band', async () => {
    api.get.mockImplementation(
      (
        _path: string,
        options?: { params: { query: { band?: string; cursor?: string } } },
      ) => {
        const band = options?.params.query.band
        const cursor = options?.params.query.cursor
        if (band === 'discoverable') {
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        }
        if (!cursor) {
          return Promise.resolve({
            data: { items: [poll({ id: 'poll-1', title: 'First' })], nextCursor: 'page-2' },
          })
        }
        return Promise.resolve({
          data: { items: [poll({ id: 'poll-2', title: 'Second' })], nextCursor: null },
        })
      },
    )
    renderPage()

    expect(await screen.findByText('First')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))

    await waitFor(() => expect(screen.getByText('Second')).toBeInTheDocument())
    expect(screen.getByText('First')).toBeInTheDocument()
  })

  it('resets pagination when switching bands', async () => {
    api.get.mockImplementation(
      (
        _path: string,
        options?: { params: { query: { band?: string; cursor?: string } } },
      ) => {
        const band = options?.params.query.band
        const cursor = options?.params.query.cursor
        if (band === 'discoverable') {
          return Promise.resolve({
            data: { items: [poll({ id: 'poll-3', title: 'Discoverable one' })], nextCursor: null },
          })
        }
        if (!cursor) {
          return Promise.resolve({
            data: { items: [poll({ id: 'poll-1', title: 'First' })], nextCursor: 'page-2' },
          })
        }
        return Promise.resolve({
          data: { items: [poll({ id: 'poll-2', title: 'Second' })], nextCursor: null },
        })
      },
    )
    renderPage()

    expect(await screen.findByText('First')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() => expect(screen.getByText('Second')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('tab', { name: 'Discover' }))
    expect(await screen.findByText('Discoverable one')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: 'Your polls' }))
    expect(await screen.findByText('First')).toBeInTheDocument()
    expect(screen.queryByText('Second')).not.toBeInTheDocument()
  })

  it('links to /polls/new to create a poll', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage()

    expect(
      await screen.findByRole('link', { name: 'New poll' }),
    ).toHaveAttribute('href', '/polls/new')
  })

  it('shows option count as a text-coded chip, not just the status colour', async () => {
    api.get.mockResolvedValue({ data: { items: [poll({ optionCount: 4 })], nextCursor: null } })
    renderPage()

    expect(await screen.findByText('4 options')).toBeInTheDocument()
  })
})
