import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))
const session = vi.hoisted(() => ({ useSession: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  ApiError: class extends Error {},
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))
vi.mock('../../auth/useSession', () => ({
  useSession: session.useSession,
  sessionQueryKey: ['session'],
}))

import '../../i18n'
import { ForgotPassword, Login } from './AuthScreens'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function renderWithRoutes(path: string) {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/forgot-password" element={<ForgotPassword />} />
        </Routes>
        <Location />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('forgot-password entry point', () => {
  it('renders a quiet forgot-password link on the login screen that navigates to the request flow_D067', () => {
    session.useSession.mockReturnValue({ data: { providers: [] }, isPending: false })
    renderWithRoutes('/login')

    fireEvent.click(screen.getByRole('link', { name: 'Forgot password' }))

    expect(screen.getByTestId('location')).toHaveTextContent('/forgot-password')
    expect(screen.getByTestId('screen-forgot-password')).toBeInTheDocument()
  })

  it('requests a password reset and shows the same check-your-inbox state regardless of the address_D067', async () => {
    api.post.mockResolvedValue({})
    renderWithRoutes('/forgot-password')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'user@example.test' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Send reset link' }).closest('form')!,
    )

    expect(await screen.findByText('Check your inbox.')).toBeInTheDocument()
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/password/forgot', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { email: 'user@example.test' },
    })
  })

  it('gates an invalid email client-side before requesting a password reset, without ever POSTing_D067', async () => {
    renderWithRoutes('/forgot-password')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'not-an-email' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })
})
