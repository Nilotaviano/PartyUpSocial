import { Navigate, NavLink, Outlet, Route, Routes } from 'react-router'
import { useTranslation } from 'react-i18next'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'
import {
  AgeGate,
  AuthCallback,
  ConfirmIdentity,
  EmailChange,
  Login,
  MagicLink,
  Register,
  ResetPassword,
  VerifyEmail,
  Welcome,
} from './features/auth/AuthScreens'
import { FindATimePage } from './features/availability/FindATimePage'
import { WeekGridPage } from './features/availability/WeekGridPage'
import { DiscoverPage } from './features/discover/DiscoverPage'
import { AddFriendPage } from './features/friends/AddFriendPage'
import { FriendsPage } from './features/friends/FriendsPage'
import { ProfilePage } from './features/friends/ProfilePage'
import { SteamImportPage } from './features/friends/SteamImportPage'
import { HomePage } from './features/home/HomePage'
import { NotificationBell } from './features/notifications/NotificationBell'
import { NotificationCenterPage } from './features/notifications/NotificationCenterPage'
import { PushEnableCard } from './features/notifications/PushEnableCard'
import { OnboardingWizard } from './features/onboarding/OnboardingWizard'
import { PublicDeletionConfirmPage } from './features/deletion/PublicDeletionConfirmPage'
import { PublicDeletionRequestPage } from './features/deletion/PublicDeletionRequestPage'
import { PollCreatePage } from './features/polls/PollCreatePage'
import { PollDetailPage } from './features/polls/PollDetailPage'
import { PollListPage } from './features/polls/PollListPage'
import { ProfileSettingsPage } from './features/profile/ProfileSettingsPage'
import { AccountSettingsPage } from './features/settings/AccountSettingsPage'
import { BlockedUsersPage } from './features/settings/BlockedUsersPage'
import { NotificationSettingsPage } from './features/settings/NotificationSettingsPage'
import { GuestSessionPage } from './features/sessions/GuestSessionPage'
import { SessionCreatePage } from './features/sessions/SessionCreatePage'
import { SessionDetailPage } from './features/sessions/SessionDetailPage'
import { SessionEditPage } from './features/sessions/SessionEditPage'
import { SessionInvitePage } from './features/sessions/SessionInvitePage'
import { SessionListPage } from './features/sessions/SessionListPage'

function AuthenticatedShell() {
  const { t } = useTranslation(['common', 'sessions', 'availability', 'home', 'polls'])

  return (
    <div className="min-h-screen bg-page text-ink">
      <header className="border-b-2 border-ink bg-card">
        <nav
          aria-label={t('navigation.label')}
          className="mx-auto flex max-w-6xl items-center gap-2 px-4 py-3 sm:px-8"
        >
          <NavLink
            aria-label={t('navigation.home')}
            className="mr-auto inline-flex min-h-11 flex-none items-center rounded-control font-display font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            end
            to="/app"
          >
            {t('app.name')}
          </NavLink>
          <div className="flex min-w-0 items-center gap-2 overflow-x-auto [-webkit-overflow-scrolling:touch] [scrollbar-width:thin]">
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/friends"
            >
              {t('navigation.friends')}
            </NavLink>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/sessions/upcoming"
            >
              {t('nav.sessions', { ns: 'sessions' })}
            </NavLink>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/polls"
            >
              {t('nav.polls', { ns: 'polls' })}
            </NavLink>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/calendar"
            >
              {t('nav.calendar', { ns: 'availability' })}
            </NavLink>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/discover"
            >
              {t('nav.discover', { ns: 'home' })}
            </NavLink>
            <NotificationBell />
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 flex-none items-center rounded-control px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'bg-primary font-semibold text-primary-ink'
                    : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
                }`
              }
              to="/settings/account"
            >
              {t('navigation.settings')}
            </NavLink>
          </div>
        </nav>
      </header>
      <PushEnableCard />
      <Outlet />
    </div>
  )
}

function LifecycleRedirect() {
  const { data: session, isPending } = useSession()

  if (isPending) {
    return null
  }

  if (!session?.authenticated) {
    return <Navigate replace to="/welcome" />
  }

  if (session.state === 'onboarding' || session.onboarding !== null) {
    return <Navigate replace to="/onboarding" />
  }

  return <Navigate replace to="/app" />
}

function App() {
  return (
    <Routes>
      <Route path="/" element={<LifecycleRedirect />} />
      <Route path="/welcome" element={<Welcome />} />
      <Route path="/age-gate" element={<AgeGate />} />
      <Route path="/login" element={<Login />} />
      <Route path="/register" element={<Register />} />
      <Route path="/auth/magic" element={<MagicLink />} />
      <Route path="/auth/callback" element={<AuthCallback />} />
      <Route path="/auth/reset" element={<ResetPassword />} />
      <Route path="/auth/confirm-identity" element={<ConfirmIdentity />} />
      <Route path="/auth/verify-email" element={<VerifyEmail />} />
      <Route path="/auth/email-change" element={<EmailChange />} />
      <Route path="/account-deletion" element={<PublicDeletionRequestPage />} />
      <Route
        path="/account-deletion/confirm"
        element={<PublicDeletionConfirmPage />}
      />

      {/*
        Guest routes (design §10.1): never gated by RequireAuth/RequireOnboarded — a
        guest is never a registered, onboarded user, and never attempts account
        onboarding or navigation that assumes identity. `GuestSessionPage` is the
        single component behind both routes.
      */}
      <Route path="/guest/invalid" element={<GuestSessionPage />} />
      <Route path="/guest/:guestLinkId" element={<GuestSessionPage />} />

      <Route element={<RequireAuth />}>
        <Route path="/onboarding" element={<OnboardingWizard />} />
        <Route path="/onboarding/username" element={<OnboardingWizard />} />
        <Route path="/onboarding/display-name" element={<OnboardingWizard />} />
        <Route path="/onboarding/avatar" element={<OnboardingWizard />} />
        <Route path="/onboarding/time-zone" element={<OnboardingWizard />} />
        <Route
          path="/onboarding/link-platforms"
          element={<OnboardingWizard />}
        />
        <Route path="/onboarding/friends" element={<OnboardingWizard />} />

        <Route element={<RequireOnboarded />}>
          <Route element={<AuthenticatedShell />}>
            <Route path="/app" element={<HomePage />} />
            <Route path="/calendar" element={<WeekGridPage />} />
            <Route path="/calendar/agenda" element={<WeekGridPage />} />
            <Route path="/find-a-time" element={<FindATimePage />} />
            <Route path="/discover" element={<DiscoverPage />} />
            <Route path="/friends" element={<FriendsPage />} />
            <Route path="/friends/import/steam" element={<SteamImportPage />} />
            <Route path="/u/:username" element={<ProfilePage />} />
            <Route path="/add/:code" element={<AddFriendPage />} />
            <Route path="/settings/profile" element={<ProfileSettingsPage />} />
            <Route path="/settings/account" element={<AccountSettingsPage />} />
            <Route path="/settings/blocked" element={<BlockedUsersPage />} />
            <Route
              path="/settings/notifications"
              element={<NotificationSettingsPage />}
            />
            <Route path="/notifications" element={<NotificationCenterPage />} />
            <Route path="/sessions/new" element={<SessionCreatePage />} />
            <Route
              path="/sessions/upcoming"
              element={<SessionListPage scope="upcoming" />}
            />
            <Route
              path="/sessions/history"
              element={<SessionListPage scope="history" />}
            />
            <Route path="/sessions/:sessionId" element={<SessionDetailPage />} />
            <Route
              path="/sessions/:sessionId/edit"
              element={<SessionEditPage />}
            />
            <Route
              path="/sessions/:sessionId/invite"
              element={<SessionInvitePage />}
            />
            <Route path="/polls/new" element={<PollCreatePage />} />
            <Route path="/polls" element={<PollListPage />} />
            <Route path="/polls/:pollId" element={<PollDetailPage />} />
          </Route>
        </Route>
      </Route>

      <Route path="*" element={<Navigate replace to="/welcome" />} />
    </Routes>
  )
}

export default App
