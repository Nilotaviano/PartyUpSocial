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

const api = vi.hoisted(() => ({
  post: vi.fn(),
  refresh: vi.fn(),
}))

vi.mock('../../api/client', () => {
  class ApiError extends Error {
    readonly status: number
    readonly code: string

    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  }

  return {
    ApiError,
    client: { POST: api.post },
    isApiError: (error: unknown) => error instanceof ApiError,
    refreshAntiforgeryToken: api.refresh,
  }
})

import '../../i18n'
import { ApiError } from '../../api/client'
import { AgeGate, MagicLink, VerifyEmail } from './AuthScreens'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function renderLink(element: React.ReactNode, path: string) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={[path]}>
        {element}
        <Location />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function session(state: 'active' | 'onboarding', continuation: string | null) {
  return {
    authenticated: true,
    userId: 'user-1',
    providers: [],
    state,
    isAdmin: false,
    onboarding:
      state === 'onboarding'
        ? { completedSteps: ['ageGate', 'auth'], nextStep: 'username' }
        : null,
    pendingDeletion: null,
    profile: null,
    continuation,
  }
}

afterEach(() => {
  cleanup()
  sessionStorage.clear()
  window.history.replaceState({}, '', '/')
  vi.clearAllMocks()
})

describe('authentication email links', () => {
  it('uses the redeemed magic-link response as continuation authority in a fresh tab_D031', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    api.post.mockResolvedValue({ data: session('active', '/add/23456789AB') })
    api.refresh.mockResolvedValue('csrf')
    window.history.replaceState(
      {},
      '',
      '/auth/magic#token=magic-token&next=%2Fu%2Fwrong.user',
    )

    renderLink(<MagicLink />, '/auth/magic')

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent(
        '/add/23456789AB',
      ),
    )
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/magic-link/consume', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { token: 'magic-token', ageGateAttested: true },
    })
    expect(window.location.hash).toBe('')
  })

  it('hydrates onboarding from the verified-email response without local continuation state_D031', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    api.post.mockResolvedValue({ data: session('onboarding', '/u/friend.one') })
    api.refresh.mockResolvedValue('csrf')
    window.history.replaceState(
      {},
      '',
      '/auth/verify-email#token=verify-token&next=%2Fadd%2Fwrongcode',
    )

    renderLink(<VerifyEmail />, '/auth/verify-email')
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'correct horse battery staple' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent(
        '/onboarding?next=%2Fu%2Ffriend.one',
      ),
    )
  })

  it('retains the token through an age-gate retry but trusts the redeemed continuation_D031', async () => {
    api.post
      .mockRejectedValueOnce(new ApiError(409, 'age_gate_required'))
      .mockResolvedValueOnce({ data: session('active', '/add/23456789AB') })
    api.refresh.mockResolvedValue('csrf')
    window.history.replaceState(
      {},
      '',
      '/auth/verify-email#token=verify-token&next=%2Fu%2Ffragment.user',
    )

    render(
      <QueryClientProvider
        client={
          new QueryClient({ defaultOptions: { queries: { retry: false } } })
        }
      >
        <MemoryRouter initialEntries={['/auth/verify-email']}>
          <Routes>
            <Route path="/auth/verify-email" element={<VerifyEmail />} />
            <Route path="/age-gate" element={<AgeGate />} />
            <Route
              path="/add/:code"
              element={<div data-testid="resolved-add" />}
            />
          </Routes>
          <Location />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'first password' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )
    await screen.findByTestId('screen-age-gate')

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await screen.findByTestId('screen-auth-verifyEmail')

    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'second password' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )

    await screen.findByTestId('resolved-add')
    expect(screen.getByTestId('location')).toHaveTextContent('/add/23456789AB')
    expect(api.post).toHaveBeenCalledTimes(2)
  })
})
