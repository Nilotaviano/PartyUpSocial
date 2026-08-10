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
import { AgeGate, Login, MagicLink, ResetPassword, VerifyEmail } from './AuthScreens'

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

  it('shows the 12-character hint, marks the password input for length validation, and wires it via aria-describedby', () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    window.history.replaceState(
      {},
      '',
      '/auth/verify-email#token=verify-token',
    )

    renderLink(<VerifyEmail />, '/auth/verify-email')

    const hint = screen.getByText('Use at least 12 characters.')
    expect(hint).toBeInTheDocument()
    const input = screen.getByLabelText('Password') as HTMLInputElement
    expect(input).toHaveAttribute('minlength', '12')
    expect(input).toHaveAttribute('aria-describedby', hint.id)
  })

  it('shows the 12-character hint on the reset-password arm too', () => {
    window.history.replaceState({}, '', '/auth/reset#token=reset-token')

    renderLink(<ResetPassword />, '/auth/reset')

    const hint = screen.getByText('Use at least 12 characters.')
    expect(hint).toBeInTheDocument()
    const input = screen.getByLabelText('Password') as HTMLInputElement
    expect(input).toHaveAttribute('minlength', '12')
    expect(input).toHaveAttribute('aria-describedby', hint.id)
  })

  it('blocks a short reset password app-side, without ever POSTing_D184', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    window.history.replaceState({}, '', '/auth/reset#token=reset-token')

    renderLink(<ResetPassword />, '/auth/reset')

    const input = screen.getByLabelText('Password') as HTMLInputElement
    fireEvent.change(input, { target: { value: 'short' } })
    fireEvent.submit(input.closest('form')!)

    expect(
      await screen.findByText('Password must be at least 12 characters.'),
    ).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('renders the specific auth_validation copy for a mocked short-password rejection, not the generic message', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    api.post.mockRejectedValue(new ApiError(400, 'auth_validation'))
    window.history.replaceState(
      {},
      '',
      '/auth/verify-email#token=verify-token',
    )

    renderLink(<VerifyEmail />, '/auth/verify-email')
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'short' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )

    expect(
      await screen.findByText('Password must be at least 12 characters.'),
    ).toBeInTheDocument()
    expect(screen.queryByText('Something went wrong.')).not.toBeInTheDocument()
  })

  it('gates an invalid email client-side before requesting a magic link', () => {
    renderLink(<Login />, '/login')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'not-an-email' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }))

    expect(api.post).not.toHaveBeenCalled()
  })

  it('blocks an empty login submit app-side with localized errors, without ever POSTing_D184', async () => {
    renderLink(<Login />, '/login')

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Enter your email address.')).toBeInTheDocument()
    expect(screen.getByText('Enter your password.')).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('rejects a malformed email on login submit with a localized app error, without ever POSTing_D184', async () => {
    renderLink(<Login />, '/login')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'not-an-email' },
    })
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'correct horse battery staple' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('shows a localized app error for the magic-link button, where reportValidity used to bubble_D184', async () => {
    renderLink(<Login />, '/login')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'not-an-email' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
    expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('renders neutral copy for a magic-link validation rejection, not the password message', async () => {
    api.post.mockRejectedValue(new ApiError(400, 'auth_validation'))
    renderLink(<Login />, '/login')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'user@example.test' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }))

    expect(
      await screen.findByText(
        'Some of the information you entered is invalid.',
      ),
    ).toBeInTheDocument()
    expect(
      screen.queryByText('Password must be at least 12 characters.'),
    ).not.toBeInTheDocument()
  })
})
