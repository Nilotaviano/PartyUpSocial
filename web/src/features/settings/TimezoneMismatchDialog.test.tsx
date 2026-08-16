import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put },
  isApiError: () => false,
}))

import '../../i18n'
import { TimezoneMismatchDialog } from './TimezoneMismatchDialog'

const DISMISSED_PAIRS_KEY = 'partyup:tz-mismatch-dismissed-pairs'

function mockSession(overrides: {
  authenticated?: boolean
  state?: string | null
  onboarding?: unknown
  timeZone?: string | null
} = {}) {
  const {
    authenticated = true,
    state = 'active',
    onboarding = null,
    timeZone = 'America/Sao_Paulo',
  } = overrides
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/auth/session') {
      return Promise.resolve({
        data: {
          authenticated,
          state,
          onboarding,
          profile: timeZone === null ? null : { timeZone },
        },
      })
    }
    throw new Error(`Unexpected GET ${path}`)
  })
}

function setBrowserTimeZone(zone: string) {
  vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
    timeZone: zone,
  } as Intl.ResolvedDateTimeFormatOptions)
}

function renderDialog() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <TimezoneMismatchDialog />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  localStorage.clear()
  api.put.mockResolvedValue({ data: {} })
})

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  vi.clearAllMocks()
})

describe('TimezoneMismatchDialog (D-197)', () => {
  it('prompts once when the browser zone differs from the profile zone_D197', async () => {
    mockSession({ timeZone: 'America/Sao_Paulo' })
    setBrowserTimeZone('America/New_York')

    renderDialog()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(
      screen.getByText('Your profile is set to America/Sao_Paulo, but this browser is currently in America/New_York.'),
    ).toBeInTheDocument()
  })

  it('does not prompt when the browser zone matches the profile zone_D197', async () => {
    mockSession({ timeZone: 'America/New_York' })
    setBrowserTimeZone('America/New_York')

    renderDialog()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/auth/session'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('never prompts during onboarding_D197', async () => {
    mockSession({ timeZone: 'America/Sao_Paulo', state: 'onboarding' })
    setBrowserTimeZone('America/New_York')

    renderDialog()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/auth/session'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('never prompts an unauthenticated (guest) session_D197', async () => {
    mockSession({ timeZone: 'America/Sao_Paulo', authenticated: false })
    setBrowserTimeZone('America/New_York')

    renderDialog()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/auth/session'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('decline persists the pair and does not re-prompt for the same pair on remount_D197', async () => {
    mockSession({ timeZone: 'America/Sao_Paulo' })
    setBrowserTimeZone('America/New_York')

    const { unmount } = renderDialog()
    fireEvent.click(await screen.findByRole('button', { name: 'Keep America/Sao_Paulo' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(JSON.parse(localStorage.getItem(DISMISSED_PAIRS_KEY) ?? '[]')).toEqual([
      'America/Sao_Paulo|America/New_York',
    ])
    expect(api.put).not.toHaveBeenCalled()

    unmount()
    renderDialog()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/auth/session'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('re-prompts on a new mismatched pair even after a prior pair was dismissed_D197', async () => {
    localStorage.setItem(DISMISSED_PAIRS_KEY, JSON.stringify(['America/Sao_Paulo|America/New_York']))
    mockSession({ timeZone: 'America/Sao_Paulo' })
    setBrowserTimeZone('Europe/Lisbon')

    renderDialog()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })

  it('accept fires the real time-zone mutation and closes_D197', async () => {
    mockSession({ timeZone: 'America/Sao_Paulo' })
    setBrowserTimeZone('America/New_York')

    renderDialog()
    fireEvent.click(await screen.findByRole('button', { name: 'Update to America/New_York' }))

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/me/time-zone',
        expect.objectContaining({ body: { timeZone: 'America/New_York' } }),
      ),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(JSON.parse(localStorage.getItem(DISMISSED_PAIRS_KEY) ?? '[]')).toEqual([
      'America/Sao_Paulo|America/New_York',
    ])
  })
})
