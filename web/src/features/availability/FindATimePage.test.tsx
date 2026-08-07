import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
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
    const items = list.querySelectorAll('li')
    expect(items).toHaveLength(3)
    expect(items[0]).toHaveTextContent('2 available')
    expect(items[1]).toHaveTextContent('1 available')
    expect(items[1]).toHaveTextContent('Sep 1')
    expect(items[2]).toHaveTextContent('Sep 3')
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

    const durationInput = await screen.findByLabelText('Duration')
    fireEvent.change(durationInput, { target: { value: '90' } })

    // The window is still rendered — the warning augments it, never
    // replaces it.
    expect(
      await screen.findByText('This session runs 30 minutes past this window.'),
    ).toBeInTheDocument()
    expect(screen.getByTestId('find-a-time-results').querySelectorAll('li')).toHaveLength(1)
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
})
