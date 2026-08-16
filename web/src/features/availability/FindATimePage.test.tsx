import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
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

import '../../i18n'
import { FindATimePage } from './FindATimePage'

function emptyMyAvailability() {
  return { occurrences: [], merged: [], range: null }
}

function defaultGetHandler(path: string) {
  if (path === '/api/v1/friends') return Promise.resolve({ data: { items: [] } })
  if (path === '/api/v1/me/availability')
    return Promise.resolve({ data: emptyMyAvailability() })
  if (path === '/api/v1/availability/overlaps')
    return Promise.resolve({
      data: { windows: [], range: { start: '', end: '' }, selectionSource: 'allFriends', consideredFriends: [] },
    })
  throw new Error(`Unexpected GET ${path}`)
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <FindATimePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockImplementation(defaultGetHandler)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('FindATimePage', () => {
  it('ranks windows by available count then maybe count_D035_D052', async () => {
    // Server-ranked order: window 2 (2 available) before window 1 (1
    // available), which itself precedes window 3 (1 available, more maybe).
    // The client must render this exact order, never re-sort it.
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [
              {
                start: '2030-09-02T10:00:00Z',
                end: '2030-09-02T12:00:00Z',
                available: [{ ordinal: 0, displayName: 'B' }, { ordinal: 1, displayName: 'C' }],
                maybe: [],
              },
              {
                start: '2030-09-01T10:00:00Z',
                end: '2030-09-01T12:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [],
              },
              {
                start: '2030-09-03T10:00:00Z',
                end: '2030-09-03T12:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [{ ordinal: 1, displayName: 'D' }],
              },
            ],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const list = await screen.findByTestId('find-a-time-results')
    // `:scope > li` — each window's own participant-name lists nest their own
    // `<li>`s inside these top-level window rows.
    const items = list.querySelectorAll(':scope > li')
    expect(items).toHaveLength(3)
    expect(items[0]).toHaveTextContent('2 available')
    expect(items[1]).toHaveTextContent('1 available')
    expect(items[1]).toHaveTextContent('Sep 1')
    expect(items[2]).toHaveTextContent('Sep 3')
  })

  it('renders the server-resolved considered friends for the all-friends selection_D110', async () => {
    // A-078: the response never echoes a per-id resolution of an explicit
    // selection, so the all-friends entry point's "whose calendars" answer
    // must come from `consideredFriends`, not the (empty, since nothing was
    // checked) local checkbox state.
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [
              { ordinal: 0, displayName: 'Ada' },
              { ordinal: 1, displayName: null },
            ],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const considered = await screen.findByTestId('find-a-time-considered-friends')
    expect(considered).toHaveTextContent('Ada')
    expect(considered).toHaveTextContent('Anonymous User')

    // The duplicated label is collapsed to one rendering — the summary text
    // above the checkboxes is the only "All accepted friends" on the page,
    // never repeated as a second label over the list.
    expect(screen.getAllByText('All accepted friends')).toHaveLength(1)
  })

  it('renders no considered-friends label or list for an empty allFriends selection_S8', async () => {
    // The default fixture already returns selectionSource 'allFriends' with
    // an empty consideredFriends array — the list itself must not render,
    // and "All accepted friends" must appear exactly once on the page (the
    // summary text above the checkboxes), never a second time as a label
    // over nothing.
    renderPage()

    expect(screen.queryByTestId('find-a-time-considered-friends')).not.toBeInTheDocument()
    expect(await screen.findAllByText('All accepted friends')).toHaveLength(1)
  })

  it('renders the local selection, not consideredFriends, for an explicit selection_D110', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friends')
        return Promise.resolve({
          data: {
            items: [
              {
                friendshipId: 'f-1',
                userId: 'friend-1',
                username: 'ada',
                displayName: 'Ada',
                avatar: { kind: 'emoji', emoji: '🎲', avatarSourceId: null, imgUrl: null },
                platforms: [],
              },
            ],
          },
        })
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [],
            range: { start: '', end: '' },
            selectionSource: 'explicit',
            consideredFriends: null,
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const checkbox = await screen.findByRole('checkbox', { name: /Ada/ })
    fireEvent.click(checkbox)

    await screen.findByText('1 selected')
    expect(screen.queryByTestId('find-a-time-considered-friends')).not.toBeInTheDocument()
  })

  it('shows the friend avatar without changing the checkbox accessible name, and toggles by click and keyboard_D050', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friends')
        return Promise.resolve({
          data: {
            items: [
              {
                friendshipId: 'f-1',
                userId: 'friend-1',
                username: 'ada',
                displayName: 'Ada',
                avatar: { kind: 'emoji', emoji: '🎲', avatarSourceId: null, imgUrl: null },
                platforms: [],
              },
            ],
          },
        })
      return defaultGetHandler(path)
    })
    renderPage()

    const checkbox = await screen.findByRole('checkbox', { name: 'Ada (@ada)' })
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

  it('warns on an over-duration window without hiding it_D109', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [
              {
                start: '2030-09-01T18:00:00Z',
                end: '2030-09-01T19:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [],
              },
            ],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const durationInput = await screen.findByLabelText('Duration (minutes)')
    fireEvent.change(durationInput, { target: { value: '90' } })

    // The window is still rendered — the warning augments it, never
    // replaces it.
    expect(
      await screen.findByText('This session runs 30 minutes past this window.'),
    ).toBeInTheDocument()
    expect(
      screen.getByTestId('find-a-time-results').querySelectorAll(':scope > li'),
    ).toHaveLength(1)
  })

  it('builds the prefill url from a recommendation_D102', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [
              {
                start: '2030-09-01T18:00:00Z',
                end: '2030-09-01T20:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [],
              },
            ],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const link = await screen.findByRole('link', { name: 'Schedule this time' })
    expect(link).toHaveAttribute(
      'href',
      '/sessions/new?startTime=2030-09-01T18%3A00%3A00Z&durationMinutes=120&overDurationMinutes=0',
    )
  })

  it('renders each window participant by name, masking the same way consideredFriends does', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [
              {
                start: '2030-09-01T18:00:00Z',
                end: '2030-09-01T20:00:00Z',
                available: [
                  { ordinal: 0, displayName: 'Ada' },
                  { ordinal: 1, displayName: null },
                ],
                maybe: [{ ordinal: 2, displayName: 'Blaine' }],
              },
            ],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const list = await screen.findByTestId('find-a-time-results')
    expect(within(list).getByText('Ada')).toBeInTheDocument()
    expect(within(list).getByText('Blaine')).toBeInTheDocument()
    // §D-076 masking: a null displayName falls back to the same anonymous
    // label consideredFriends renders, never a client-invented name.
    expect(within(list).getByText('Anonymous User')).toBeInTheDocument()
  })

  it('shows a loading affordance for the friends checklist while it fetches', async () => {
    let resolveFriends!: (value: { data: { items: unknown[] } }) => void
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/friends') {
        return new Promise((resolve) => {
          resolveFriends = resolve
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    expect(await screen.findByTestId('find-a-time-friends-loading')).toHaveTextContent(
      'Loading...',
    )

    resolveFriends({ data: { items: [] } })
    await screen.findByRole('heading', { name: 'Friends' })
  })

  it('accumulates selected windows into a repeated startTimes poll-arm url_D102', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/overlaps') {
        return Promise.resolve({
          data: {
            windows: [
              {
                start: '2030-09-01T18:00:00Z',
                end: '2030-09-01T20:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [],
              },
              {
                start: '2030-09-02T18:00:00Z',
                end: '2030-09-02T20:00:00Z',
                available: [{ ordinal: 0, displayName: 'A' }],
                maybe: [],
              },
            ],
            range: { start: '', end: '' },
            selectionSource: 'allFriends',
            consideredFriends: [],
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    // Nothing selected yet: no poll-arm CTA at all — the arm never appears empty.
    expect(screen.queryByText(/Create poll with selected times/)).not.toBeInTheDocument()

    const addToPollBoxes = await screen.findAllByLabelText('Add to poll')
    fireEvent.click(addToPollBoxes[0])
    fireEvent.click(addToPollBoxes[1])

    expect(await screen.findByText('2 times selected for a poll')).toBeInTheDocument()
    const pollLink = screen.getByRole('link', { name: 'Create poll with selected times' })
    expect(pollLink).toHaveAttribute(
      'href',
      '/polls/new?startTimes=2030-09-01T18%3A00%3A00Z&startTimes=2030-09-02T18%3A00%3A00Z',
    )

    // Deselecting one window removes it from the accumulated set, never the other.
    fireEvent.click(addToPollBoxes[0])
    expect(await screen.findByText('1 time selected for a poll')).toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: 'Create poll with selected times' }),
    ).toHaveAttribute('href', '/polls/new?startTimes=2030-09-02T18%3A00%3A00Z')
  })
})
