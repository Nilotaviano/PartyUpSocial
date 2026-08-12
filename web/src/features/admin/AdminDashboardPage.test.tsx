import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

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
import { AdminDashboardPage } from './AdminDashboardPage'

function usageWindow(days: number, overrides: Record<string, unknown> = {}) {
  return {
    days,
    since: '2026-08-01T00:00:00Z',
    registrations: 12,
    activeUsers: 9,
    sessionsCreated: 7,
    pollsCreated: 4,
    confirmationDenominator: 8,
    confirmationSuccesses: 6,
    confirmationRate: 0.75,
    ...overrides,
  }
}

function dashboard(overrides: Record<string, unknown> = {}) {
  return {
    generatedAt: '2026-08-12T10:00:00Z',
    last7Days: usageWindow(7),
    last30Days: usageWindow(30, {
      registrations: 40,
      activeUsers: 31,
      sessionsCreated: 22,
      pollsCreated: 11,
      confirmationDenominator: 25,
      confirmationSuccesses: 20,
      confirmationRate: 0.8,
    }),
    repeatScheduling: { days: 30, organizers: 10, repeatOrganizers: 3, rate: 0.3 },
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <AdminDashboardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AdminDashboardPage — D-178 rendering', () => {
  it('renders both the 7-day and 30-day windows with every launch measure_D178', async () => {
    api.get.mockResolvedValue({ data: dashboard() })

    renderPage()

    const sevenDay = await screen.findByRole('region', { name: 'Last 7 days' })
    const thirtyDay = screen.getByRole('region', { name: 'Last 30 days' })

    for (const label of [
      'Registrations',
      'Active users',
      'Sessions created',
      'Polls created',
      'Proposals',
      'Confirmed',
      'Confirmation rate',
    ]) {
      expect(within(sevenDay).getByText(label)).toBeTruthy()
      expect(within(thirtyDay).getByText(label)).toBeTruthy()
    }

    expect(within(sevenDay).getByText('12')).toBeTruthy()
    expect(within(thirtyDay).getByText('40')).toBeTruthy()
  })

  it('shows the raw organizer counts beside the repeat rate_D178', async () => {
    api.get.mockResolvedValue({ data: dashboard() })

    renderPage()

    const repeat = await screen.findByRole('region', { name: 'Repeat scheduling' })
    expect(within(repeat).getByText('Repeat rate')).toBeTruthy()
    expect(within(repeat).getByText('Organizers')).toBeTruthy()
    expect(within(repeat).getByText('Repeat organizers')).toBeTruthy()
    expect(within(repeat).getByText('10')).toBeTruthy()
    expect(within(repeat).getByText('3')).toBeTruthy()
  })

  // PHASE8_DESIGN.md §3 S3 "Dashboard rendering caveats": `sessionsCreated`
  // already contains poll-derived sessions, so the page must never present
  // `sessionsCreated + pollsCreated` as a proposals figure — the proposals
  // figure IS the confirmation denominator.
  it('never renders sessionsCreated + pollsCreated as the proposals figure_D178', async () => {
    api.get.mockResolvedValue({
      data: dashboard({
        last7Days: usageWindow(7, {
          sessionsCreated: 7,
          pollsCreated: 4,
          confirmationDenominator: 8,
        }),
      }),
    })

    renderPage()

    const sevenDay = await screen.findByRole('region', { name: 'Last 7 days' })
    // The naive sum (11) appears nowhere; the denominator (8) is what
    // "Proposals" reads.
    expect(within(sevenDay).queryByText('11')).toBeNull()
    expect(within(sevenDay).getByText('8')).toBeTruthy()
  })

  it('renders a placeholder rather than a rate when the denominator is empty_D178', async () => {
    api.get.mockResolvedValue({
      data: dashboard({
        last7Days: usageWindow(7, {
          confirmationDenominator: 0,
          confirmationSuccesses: 0,
          confirmationRate: null,
        }),
      }),
    })

    renderPage()

    const sevenDay = await screen.findByRole('region', { name: 'Last 7 days' })
    expect(within(sevenDay).getByLabelText('No value yet')).toBeTruthy()
  })
})

describe('AdminDashboardPage — D-177 fail-closed parity', () => {
  it('renders a role=alert not-found message on a 404 and names no admin surface_D177', async () => {
    api.get.mockRejectedValue(new TestApiError(404, 'not_found'))

    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toBe('This page is not available.')
    expect(screen.queryByText('Usage dashboard')).toBeNull()
    expect(document.body.textContent?.toLowerCase()).not.toContain('admin')
  })
})

describe('AdminDashboardPage — in-admin navigation', () => {
  it('links account management from the dashboard', async () => {
    api.get.mockResolvedValue({ data: dashboard() })

    renderPage()

    const link = await screen.findByRole('link', { name: 'Manage accounts' })
    expect(link.getAttribute('href')).toBe('/admin/users')
  })

  it('renders no admin link on the fail-closed branch_D177', async () => {
    api.get.mockRejectedValue(new TestApiError(404, 'not_found'))

    renderPage()

    await screen.findByRole('alert')
    expect(screen.queryByRole('link')).toBeNull()
  })
})
