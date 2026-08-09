import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  ApiError: class ApiError extends Error {},
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { consumeReauthProof } from '../../auth/reauthProof'
import { setPendingSettingsAction } from '../settings/reauthAction'
import { AgeGate, AuthCallback } from './AuthScreens'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function renderCallback(search: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/auth/callback' + search]}>
        <AuthCallback />
        <Location />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function activeSession() {
  return {
    authenticated: true,
    userId: 'u1',
    providers: [],
    state: 'active',
    isAdmin: false,
    onboarding: null,
    pendingDeletion: null,
    profile: null,
  }
}

afterEach(() => {
  cleanup()
  consumeReauthProof()
  sessionStorage.clear()
  window.history.replaceState({}, '', '/')
  vi.clearAllMocks()
})

describe('AuthCallback', () => {
  it('routes a reauth return with a pending platform unlink to profile settings even when next points at account', async () => {
    api.get.mockResolvedValue({ data: activeSession() })
    setPendingSettingsAction({ kind: 'unlinkPlatform', id: 'link-1' })
    window.history.replaceState(
      {},
      '',
      '/auth/callback?next=%2Fsettings%2Faccount#reauth=proof-1',
    )

    renderCallback('?next=%2Fsettings%2Faccount')

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent(
        '/settings/profile',
      ),
    )
    expect(consumeReauthProof()).toBe('proof-1')
  })

  it.each([
    ['?next=%2Fu%2FFriend.One', '/u/friend.one'],
    ['?next=%2Fadd%2F2345-6789-ab', '/add/23456789AB'],
  ])(
    'restores an active friend continuation_D031',
    async (search, expected) => {
      api.get.mockResolvedValue({ data: activeSession() })
      window.history.replaceState({}, '', '/auth/callback' + search)

      renderCallback(search)

      await waitFor(() =>
        expect(screen.getByTestId('location')).toHaveTextContent(expected),
      )
    },
  )

  it('carries a friend continuation into onboarding for an incomplete account_D031', async () => {
    api.get.mockResolvedValue({
      data: {
        ...activeSession(),
        state: 'onboarding',
        onboarding: {
          completedSteps: ['ageGate', 'auth'],
          nextStep: 'username',
        },
      },
    })
    window.history.replaceState(
      {},
      '',
      '/auth/callback?next=%2Fadd%2F23456789AB',
    )

    renderCallback('?next=%2Fadd%2F23456789AB')

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent(
        '/onboarding?next=%2Fadd%2F23456789AB',
      ),
    )
  })

  it('drops a hostile authentication return_D031', async () => {
    api.get.mockResolvedValue({ data: activeSession() })
    window.history.replaceState(
      {},
      '',
      '/auth/callback?next=https%3A%2F%2Fevil.test',
    )

    renderCallback('?next=https%3A%2F%2Fevil.test')

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent('/app'),
    )
  })

  it('routes an age-gate requirement through the shipped screen instead of a silent session fetch, and preserves the continuation_D083', async () => {
    api.get.mockResolvedValue({ data: activeSession() })
    window.history.replaceState(
      {},
      '',
      '/auth/callback?status=age_gate_required&next=%2Fadd%2FXQYENU8937',
    )

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter
          initialEntries={[
            '/auth/callback?status=age_gate_required&next=%2Fadd%2FXQYENU8937',
          ]}
        >
          <Routes>
            <Route path="/auth/callback" element={<AuthCallback />} />
            <Route path="/age-gate" element={<AgeGate />} />
          </Routes>
          <Location />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    await screen.findByTestId('screen-age-gate')
    expect(api.get).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/login?next=%2Fadd%2FXQYENU8937',
    )
  })
})
