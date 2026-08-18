import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

class TestApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string) {
    super(code)
    this.status = status
    this.code = code
  }
}

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) => error instanceof TestApiError,
}))

import '../../i18n'
import { AdminFeedbackPage } from './AdminFeedbackPage'

function feedbackItem(overrides: Record<string, unknown> = {}) {
  return {
    id: '0198c2f0-0000-7000-8000-000000000001',
    type: 'bug',
    message: 'The week grid scrolls past the header on iOS.',
    submitterUserId: '0198c2f0-0000-7000-8000-0000000000aa',
    route: '/polls/abc',
    userAgent: 'Mozilla/5.0 (iPhone)',
    locale: 'pt-BR',
    appVersion: null,
    createdAt: '2026-08-18T10:00:00Z',
    handled: false,
    handledAt: null,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}
    >
      <MemoryRouter>
        <AdminFeedbackPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.post.mockResolvedValue({ data: undefined })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AdminFeedbackPage — D-214 list rendering', () => {
  it('renders the message, the submitter and every attached context field_D214', async () => {
    api.get.mockResolvedValue({
      data: { items: [feedbackItem()], page: 1, pageSize: 50, total: 1 },
    })

    renderPage()

    expect(
      await screen.findByText('The week grid scrolls past the header on iOS.'),
    ).toBeInTheDocument()
    expect(screen.getByText('0198c2f0-0000-7000-8000-0000000000aa')).toBeInTheDocument()
    expect(screen.getByText('/polls/abc')).toBeInTheDocument()
    expect(screen.getByText('Mozilla/5.0 (iPhone)')).toBeInTheDocument()
    expect(screen.getByText('pt-BR')).toBeInTheDocument()
    // D-212 makes every context field optional: an absent one reads as absent, not blank.
    expect(screen.getByText('Not attached')).toBeInTheDocument()
    expect(screen.getByText('Not handled')).toBeInTheDocument()
  })

  it('names an anonymous submission rather than showing an empty submitter_D212_D214', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [feedbackItem({ submitterUserId: null, route: null, userAgent: null, locale: null })],
        page: 1,
        pageSize: 50,
        total: 1,
      },
    })

    renderPage()

    expect(await screen.findByText('Anonymous')).toBeInTheDocument()
  })

  it('reports a handled row with its handled timestamp_D214', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [feedbackItem({ handled: true, handledAt: '2026-08-18T12:00:00Z' })],
        page: 1,
        pageSize: 50,
        total: 1,
      },
    })

    renderPage()

    expect(await screen.findByRole('button', { name: 'Mark unhandled' })).toBeInTheDocument()
    // Anchored past the "Handled state" filter label, which also starts with "Handled".
    expect(screen.getByText(/^Handled (?!state)/)).toBeInTheDocument()
  })
})

describe('AdminFeedbackPage — D-214 filters and the Handled toggle', () => {
  it('sends the type and handled filters to the server rather than filtering locally_D214', async () => {
    api.get.mockResolvedValue({ data: { items: [], page: 1, pageSize: 50, total: 0 } })

    renderPage()

    await screen.findByText('No feedback matched those filters.')
    expect(api.get).toHaveBeenLastCalledWith('/api/v1/admin/feedback', {
      params: { query: { type: undefined, handled: undefined, page: 1 } },
    })

    fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'idea' } })
    await waitFor(() =>
      expect(api.get).toHaveBeenLastCalledWith('/api/v1/admin/feedback', {
        params: { query: { type: 'idea', handled: undefined, page: 1 } },
      }),
    )

    fireEvent.change(screen.getByLabelText('Handled state'), { target: { value: 'unhandled' } })
    await waitFor(() =>
      expect(api.get).toHaveBeenLastCalledWith('/api/v1/admin/feedback', {
        params: { query: { type: 'idea', handled: false, page: 1 } },
      }),
    )
  })

  it('toggles a row Handled through the D-214 endpoint_D214', async () => {
    api.get.mockResolvedValue({
      data: { items: [feedbackItem()], page: 1, pageSize: 50, total: 1 },
    })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Mark handled' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/admin/feedback/{id}/handled', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { id: '0198c2f0-0000-7000-8000-000000000001' },
        },
        body: { handled: true },
      }),
    )
  })
})

describe('AdminFeedbackPage — D-177 fail-closed parity', () => {
  it('renders a role=alert not-found message on a 404 and names no admin surface_D177', async () => {
    api.get.mockRejectedValue(new TestApiError(404, 'not_found'))

    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toBe('This page is not available.')
    expect(document.body.textContent?.toLowerCase()).not.toContain('admin')
  })
})
