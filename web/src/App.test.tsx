import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import './i18n'
import type { Session } from './api/client'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'

vi.mock('./auth/useSession', () => ({ useSession: vi.fn() }))

const mockedUseSession = vi.mocked(useSession)

function session(overrides: Partial<Session> = {}): Session {
  return {
    authenticated: false,
    userId: null,
    providers: [],
    state: null,
    isAdmin: false,
    onboarding: null,
    pendingDeletion: null,
    profile: null,
    ...overrides,
  }
}

function LocationIndicator() {
  const location = useLocation()
  return <output data-testid="location">{`${location.pathname}${location.search}`}</output>
}

function renderRoute(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <App />
        <LocationIndicator />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function setSession(value: Session) {
  mockedUseSession.mockReturnValue({ data: value, isPending: false } as ReturnType<typeof useSession>)
}

afterEach(() => {
  cleanup()
  mockedUseSession.mockReset()
})

describe('route guards', () => {
  it('redirects an unauthenticated protected route to login with next', async () => {
    setSession(session())
    renderRoute('/app')

    await waitFor(() => expect(screen.getByTestId('screen-login')).toBeInTheDocument())
    expect(screen.getByTestId('location')).toHaveTextContent('/login?next=%2Fapp')
  })

  it('redirects an onboarding session away from onboarded routes', async () => {
    setSession(session({ authenticated: true, state: 'onboarding', onboarding: { completedSteps: [], nextStep: 'username' } }))
    renderRoute('/settings/profile')

    await waitFor(() => expect(screen.getByTestId('screen-onboarding')).toBeInTheDocument())
  })

  it('does not render protected content while the session is loading', () => {
    mockedUseSession.mockReturnValue({ data: undefined, isPending: true } as ReturnType<typeof useSession>)
    renderRoute('/app')

    expect(screen.queryByTestId('screen-app')).not.toBeInTheDocument()
  })

  it.each([
    ['RequireAuth', RequireAuth],
    ['RequireOnboarded', RequireOnboarded],
  ])('%s does not render children while the session is loading', (_name, Guard) => {
    mockedUseSession.mockReturnValue({ data: undefined, isPending: true } as ReturnType<typeof useSession>)
    render(
      <MemoryRouter>
        <Guard>
          <div data-testid="guard-child" />
        </Guard>
      </MemoryRouter>,
    )

    expect(screen.queryByTestId('guard-child')).not.toBeInTheDocument()
  })

  it.each([
    [session(), 'screen-welcome'],
    [session({ authenticated: true, state: 'onboarding', onboarding: { completedSteps: [], nextStep: 'username' } }), 'screen-onboarding'],
    [session({ authenticated: true, state: 'Active' }), 'screen-app'],
  ])('redirects the root lifecycle route to %s', async (value, expectedScreen) => {
    setSession(value)
    renderRoute('/')

    await waitFor(() => expect(screen.getByTestId(expectedScreen)).toBeInTheDocument())
  })
})
