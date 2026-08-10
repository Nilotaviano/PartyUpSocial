import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import i18n from './i18n'
import { client, type Session } from './api/client'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'

vi.mock('./auth/useSession', () => ({
  useSession: vi.fn(),
  useLogoutMutation: () => ({ isPending: false, mutate: vi.fn() }),
}))

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
  return (
    <output data-testid="location">{`${location.pathname}${location.search}`}</output>
  )
}

function renderRoute(path: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
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
  mockedUseSession.mockReturnValue({
    data: value,
    isPending: false,
  } as ReturnType<typeof useSession>)
}

afterEach(async () => {
  cleanup()
  mockedUseSession.mockReset()
  await i18n.changeLanguage('en')
})

describe('route guards', () => {
  it('shows configured Google and Steam sign-in methods_D067', () => {
    setSession(
      session({ providers: ['password', 'magicLink', 'google', 'steam'] }),
    )
    renderRoute('/login')

    expect(
      screen.getByRole('button', { name: 'Continue with Google' }),
    ).toBeVisible()
    expect(
      screen.getByRole('button', { name: 'Continue with Steam' }),
    ).toBeVisible()
  })

  it('redirects an unauthenticated protected route to login with next', async () => {
    setSession(session())
    renderRoute('/app')

    await waitFor(() =>
      expect(screen.getByTestId('screen-login')).toBeInTheDocument(),
    )
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/login?next=%2Fapp',
    )
  })

  it('redirects an onboarding session away from onboarded routes', async () => {
    setSession(
      session({
        authenticated: true,
        state: 'onboarding',
        onboarding: { completedSteps: [], nextStep: 'username' },
      }),
    )
    renderRoute('/settings/profile')

    await waitFor(() =>
      expect(screen.getByTestId('screen-onboarding')).toBeInTheDocument(),
    )
  })

  it('does not render protected content while the session is loading', () => {
    mockedUseSession.mockReturnValue({
      data: undefined,
      isPending: true,
    } as ReturnType<typeof useSession>)
    renderRoute('/app')

    expect(screen.queryByTestId('screen-app')).not.toBeInTheDocument()
  })

  it.each([
    ['RequireAuth', RequireAuth],
    ['RequireOnboarded', RequireOnboarded],
  ])(
    '%s does not render children while the session is loading',
    (_name, Guard) => {
      mockedUseSession.mockReturnValue({
        data: undefined,
        isPending: true,
      } as ReturnType<typeof useSession>)
      render(
        <MemoryRouter>
          <Guard>
            <div data-testid="guard-child" />
          </Guard>
        </MemoryRouter>,
      )

      expect(screen.queryByTestId('guard-child')).not.toBeInTheDocument()
    },
  )

  it.each([
    [session(), 'screen-welcome'],
    [
      session({
        authenticated: true,
        state: 'onboarding',
        onboarding: { completedSteps: [], nextStep: 'username' },
      }),
      'screen-onboarding',
    ],
    [session({ authenticated: true, state: 'Active' }), 'screen-app'],
  ])(
    'redirects the root lifecycle route to %s',
    async (value, expectedScreen) => {
      setSession(value)
      renderRoute('/')

      await waitFor(() =>
        expect(screen.getByTestId(expectedScreen)).toBeInTheDocument(),
      )
    },
  )
})

describe('Slice 7 friend route reachability', () => {
  it.each([
    ['/u/friend.one', 'screen-friend-profile'],
    ['/add/23456789AB', 'screen-add-friend'],
  ])('renders %s only for an Active session_D031', async (path, testId) => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute(path)

    await waitFor(() => expect(screen.getByTestId(testId)).toBeInTheDocument())
  })

  it('preserves a canonical friend route when onboarding is required_D031', async () => {
    setSession(
      session({
        authenticated: true,
        state: 'onboarding',
        onboarding: { completedSteps: [], nextStep: 'username' },
      }),
    )
    renderRoute('/u/Friend.One')

    await waitFor(() =>
      expect(screen.getByTestId('screen-onboarding')).toBeInTheDocument(),
    )
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/onboarding/username?next=%2Fu%2Ffriend.one',
    )
  })
})
describe('AppNavigationTests', () => {
  it.each([
    ['/app', 'screen-app'],
    ['/friends', 'screen-friends'],
    ['/friends/import/steam', 'screen-steam-import'],
    ['/u/friend.one', 'screen-friend-profile'],
    ['/add/23456789AB', 'screen-add-friend'],
    ['/settings/profile', 'screen-settings-profile'],
    ['/settings/account', 'screen-settings-account'],
    ['/settings/blocked', 'screen-settings-blocked'],
    ['/sessions/upcoming', 'screen-sessions-upcoming'],
    ['/sessions/history', 'screen-sessions-history'],
    ['/sessions/new', 'screen-session-create'],
    ['/calendar', 'screen-calendar'],
    ['/calendar/agenda', 'screen-calendar'],
    ['/find-a-time', 'screen-find-a-time'],
    ['/discover', 'screen-discover'],
    ['/polls', 'screen-polls'],
    ['/polls/new', 'screen-poll-create'],
  ])(
    'AuthenticatedEntries_ReachFriendsAndBlockedRoutes_D127: %s',
    async (path, testId) => {
      setSession(session({ authenticated: true, state: 'active' }))
      renderRoute(path)

      expect(await screen.findByTestId(testId)).toBeInTheDocument()
      const friends = screen.getByRole('link', { name: 'Friends' })
      friends.focus()
      expect(friends).toHaveFocus()
      if (path.startsWith('/settings/')) {
        expect(
          screen.getByRole('link', { name: 'Blocked users' }),
        ).toBeInTheDocument()
      }
    },
  )

  it('clicked entries render the guarded Friends, Account, and Blocked pages', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    fireEvent.click(screen.getByRole('link', { name: 'Friends' }))
    expect(await screen.findByTestId('screen-friends')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('link', { name: 'Find Steam friends' }))
    expect(await screen.findByTestId('screen-steam-import')).toBeInTheDocument()

    fireEvent.pointerDown(screen.getByRole('button', { name: 'Account menu' }), {
      button: 0,
      pointerType: 'mouse',
    })
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Settings' }))
    expect(
      await screen.findByTestId('screen-settings-account'),
    ).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('link', { name: 'Blocked users' }))
    expect(
      await screen.findByTestId('screen-settings-blocked'),
    ).toBeInTheDocument()
  })

  it('clicked Sessions entry renders the guarded upcoming sessions page', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    fireEvent.click(screen.getByRole('link', { name: 'Sessions' }))
    expect(
      await screen.findByTestId('screen-sessions-upcoming'),
    ).toBeInTheDocument()
  })

  it('clicked Calendar entry renders the guarded week grid, not a second nav entry for agenda_S6', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    fireEvent.click(screen.getByRole('link', { name: 'Calendar' }))
    expect(await screen.findByTestId('screen-calendar')).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Agenda' }),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Agenda' })).toBeInTheDocument()
  })

  it.each([
    ['en', 'Friends', 'Account menu', 'Settings', 'Sessions', 'Calendar', 'Discover'],
    ['pt-BR', 'Amigos', 'Menu da conta', 'Configurações', 'Sessões', 'Calendário', 'Descobrir'],
    ['es', 'Amistades', 'Menú de la cuenta', 'Configuración', 'Sesiones', 'Calendario', 'Descubrir'],
  ])(
    'PersistentEntries_HaveLocalizedAccessibleLabelsAndCurrentState_D139: %s',
    async (
      locale,
      friendsLabel,
      accountMenuLabel,
      settingsLabel,
      sessionsLabel,
      calendarLabel,
      discoverLabel,
    ) => {
      await i18n.changeLanguage(locale)
      setSession(session({ authenticated: true, state: 'active' }))
      renderRoute('/friends')

      const link = await screen.findByRole('link', { name: friendsLabel })
      expect(link).toHaveAttribute('aria-current', 'page')
      expect(screen.getByRole('link', { name: sessionsLabel })).toHaveAttribute(
        'href',
        '/sessions/upcoming',
      )
      expect(screen.getByRole('link', { name: calendarLabel })).toHaveAttribute(
        'href',
        '/calendar',
      )
      expect(screen.getByRole('link', { name: discoverLabel })).toHaveAttribute(
        'href',
        '/discover',
      )

      // Opened last: Radix's dropdown menu marks the rest of the page `aria-hidden` while open,
      // so the persistent nav links above are no longer queryable by role once this fires.
      const accountMenuTrigger = screen.getByRole('button', { name: accountMenuLabel })
      fireEvent.pointerDown(accountMenuTrigger, { button: 0, pointerType: 'mouse' })
      expect(
        await screen.findByRole('menuitem', { name: settingsLabel }),
      ).toHaveAttribute('href', '/settings/account')
    },
  )

  it('clicked Discover entry renders the guarded discover page_S7', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    fireEvent.click(screen.getByRole('link', { name: 'Discover' }))
    expect(await screen.findByTestId('screen-discover')).toBeInTheDocument()
  })

  it('clicked Polls entry renders the guarded poll list page_P6S5', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    fireEvent.click(screen.getByRole('link', { name: 'Polls' }))
    expect(await screen.findByTestId('screen-polls')).toBeInTheDocument()
  })
})

describe('Guest route reachability_S6', () => {
  it('renders /guest/invalid with no session and no query_D044', async () => {
    const getSpy = vi.spyOn(client, 'GET')
    try {
      renderRoute('/guest/invalid')

      expect(
        await screen.findByTestId('screen-guest-invalid'),
      ).toBeInTheDocument()
      expect(
        screen.getByRole('heading', { name: 'This link is not valid.' }),
      ).toBeInTheDocument()
      expect(getSpy).not.toHaveBeenCalled()
    } finally {
      getSpy.mockRestore()
    }
  })

  it('reaches the tokenless guest route without any auth guard', async () => {
    renderRoute('/guest/11111111-1111-1111-1111-111111111111')

    await waitFor(() => {
      expect(
        screen.queryByTestId('screen-guest-session') ??
          screen.queryByTestId('screen-guest-invalid') ??
          screen.queryByTestId('screen-guest-terminal'),
      ).not.toBeNull()
    })
    expect(screen.queryByTestId('screen-welcome')).not.toBeInTheDocument()
  })
})
