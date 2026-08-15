import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
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
      // TopBar and BottomTabBar both render the section nav (one per viewport
      // breakpoint, both present in jsdom) — scope to TopBar's landmark by its
      // distinct accessible name so this resolves a single "Friends" link.
      const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
      const friends = within(nav).getByRole('link', { name: 'Friends' })
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

    const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
    fireEvent.click(within(nav).getByRole('link', { name: 'Friends' }))
    expect(await screen.findByTestId('screen-friends')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('link', { name: 'Find Steam friends' }))
    expect(await screen.findByTestId('screen-steam-import')).toBeInTheDocument()

    // S1's AccountMenu is now a Radix Dialog-backed Sheet: it opens on click (not
    // pointerdown), and its rows are `link`s inside a `dialog`, not `menuitem`s.
    fireEvent.click(screen.getByRole('button', { name: 'Account menu' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('link', { name: 'Settings' }))
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

    const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
    fireEvent.click(within(nav).getByRole('link', { name: 'Sessions' }))
    expect(
      await screen.findByTestId('screen-sessions-upcoming'),
    ).toBeInTheDocument()
  })

  it('clicked Calendar entry renders the guarded week grid, not a second nav entry for agenda_S6', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
    fireEvent.click(within(nav).getByRole('link', { name: 'Calendar' }))
    expect(await screen.findByTestId('screen-calendar')).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Agenda' }),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Agenda' })).toBeInTheDocument()
  })

  it.each([
    ['en', 'Friends', 'Account menu', 'Settings', 'Sessions', 'Calendar', 'Primary navigation'],
    [
      'pt-BR',
      'Amigos',
      'Menu da conta',
      'Configurações',
      'Sessões',
      'Calendário',
      'Navegação principal',
    ],
    [
      'es',
      'Amistades',
      'Menú de la cuenta',
      'Configuración',
      'Sesiones',
      'Calendario',
      'Navegación principal',
    ],
  ])(
    'PersistentEntries_HaveLocalizedAccessibleLabelsAndCurrentState_D139: %s',
    async (
      locale,
      friendsLabel,
      accountMenuLabel,
      settingsLabel,
      sessionsLabel,
      calendarLabel,
      navLabel,
    ) => {
      await i18n.changeLanguage(locale)
      setSession(session({ authenticated: true, state: 'active' }))
      renderRoute('/friends')

      // TopBar and BottomTabBar both render the section nav (S2: distinct
      // accessible names, per axe's landmark-unique rule) — scope to TopBar's
      // landmark so each assertion below resolves a single element.
      const nav = within(await screen.findByRole('navigation', { name: navLabel }))
      const link = nav.getByRole('link', { name: friendsLabel })
      expect(link).toHaveAttribute('aria-current', 'page')
      expect(nav.getByRole('link', { name: sessionsLabel })).toHaveAttribute(
        'href',
        '/sessions/upcoming',
      )
      expect(nav.getByRole('link', { name: calendarLabel })).toHaveAttribute(
        'href',
        '/calendar',
      )

      // Opened last: Radix's Dialog-backed Sheet marks the rest of the page `aria-hidden` while
      // open, so the persistent nav links above are no longer queryable by role once this fires.
      const accountMenuTrigger = screen.getByRole('button', { name: accountMenuLabel })
      fireEvent.click(accountMenuTrigger)
      const dialog = await screen.findByRole('dialog')
      expect(
        within(dialog).getByRole('link', { name: settingsLabel }),
      ).toHaveAttribute('href', '/settings/account')
    },
  )

  it('clicked Discover entry renders the guarded discover page_S7', async () => {
    // Owner ruling: Discover is no longer a persistent nav entry — it lives on Home's
    // discover section (`data-tour="discover"`), so this now drives the flow from there.
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    const discoverRegion = await screen.findByTestId('home-region-discover')
    fireEvent.click(within(discoverRegion).getByRole('link', { name: 'See all' }))
    expect(await screen.findByTestId('screen-discover')).toBeInTheDocument()
  })

  it('clicked Polls entry renders the guarded poll list page_P6S5', async () => {
    setSession(session({ authenticated: true, state: 'active' }))
    renderRoute('/app')

    const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
    fireEvent.click(within(nav).getByRole('link', { name: 'Polls' }))
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

describe('legal routes and footer_S5', () => {
  const routes = [
    ['/privacy', 'screen-privacy'],
    ['/terms', 'screen-terms'],
    ['/community-standards', 'screen-community-standards'],
  ] as const

  for (const [path, testId] of routes) {
    it(`serves ${path} to an unauthenticated visitor`, async () => {
      setSession(session())
      renderRoute(path)

      expect(await screen.findByTestId(testId)).toBeInTheDocument()
      expect(screen.getByTestId('location')).toHaveTextContent(path)
      expect(screen.queryByTestId('screen-login')).not.toBeInTheDocument()
    })
  }

  it('surfaces the public deletion route in the footer of the welcome screen_D072', async () => {
    setSession(session())
    renderRoute('/welcome')

    const footer = await screen.findByRole('navigation', {
      name: 'Legal and support',
    })
    expect(
      within(footer).getByRole('link', { name: 'Delete your account' }),
    ).toHaveAttribute('href', '/account-deletion')
  })

  it('renders the footer inside the authenticated shell too', async () => {
    setSession(session({ authenticated: true, state: 'active', userId: 'u1' }))
    renderRoute('/app')

    expect(
      await screen.findByRole('navigation', { name: 'Legal and support' }),
    ).toBeInTheDocument()
  })
})
