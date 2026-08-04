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
import { AddFriendPage } from './features/friends/AddFriendPage'
import { FriendsPage } from './features/friends/FriendsPage'
import { ProfilePage } from './features/friends/ProfilePage'
import { SteamImportPage } from './features/friends/SteamImportPage'
import { OnboardingWizard } from './features/onboarding/OnboardingWizard'
import { PublicDeletionConfirmPage } from './features/deletion/PublicDeletionConfirmPage'
import { PublicDeletionRequestPage } from './features/deletion/PublicDeletionRequestPage'
import { ProfileSettingsPage } from './features/profile/ProfileSettingsPage'
import { AccountSettingsPage } from './features/settings/AccountSettingsPage'
import { BlockedUsersPage } from './features/settings/BlockedUsersPage'

type PlaceholderProps = {
  testId: string
  translationKey: string
}

function Placeholder({ testId, translationKey }: PlaceholderProps) {
  const { t } = useTranslation([
    'common',
    'auth',
    'onboarding',
    'profile',
    'settings',
    'errors',
  ])

  return (
    <main data-testid={testId}>
      <h1>{t(translationKey)}</h1>
    </main>
  )
}

function AuthenticatedShell() {
  const { t } = useTranslation('common')

  return (
    <div className="min-h-screen bg-slate-50 text-slate-950">
      <header className="border-b border-slate-200 bg-white">
        <nav
          aria-label={t('navigation.label')}
          className="mx-auto flex max-w-6xl items-center gap-2 px-4 py-3 sm:px-8"
        >
          <NavLink
            aria-label={t('navigation.home')}
            className="mr-auto inline-flex min-h-11 items-center rounded-md font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            end
            to="/app"
          >
            {t('app.name')}
          </NavLink>
          <NavLink
            className={({ isActive }) =>
              `inline-flex min-h-11 items-center rounded-md px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 ${
                isActive
                  ? 'bg-indigo-100 font-semibold text-indigo-950'
                  : 'text-slate-700 hover:bg-slate-100'
              }`
            }
            to="/friends"
          >
            {t('navigation.friends')}
          </NavLink>
          <NavLink
            className={({ isActive }) =>
              `inline-flex min-h-11 items-center rounded-md px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 ${
                isActive
                  ? 'bg-indigo-100 font-semibold text-indigo-950'
                  : 'text-slate-700 hover:bg-slate-100'
              }`
            }
            to="/settings/account"
          >
            {t('navigation.settings')}
          </NavLink>
        </nav>
      </header>
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
            <Route
              path="/app"
              element={
                <Placeholder testId="screen-app" translationKey="pages.app" />
              }
            />
            <Route path="/friends" element={<FriendsPage />} />
            <Route path="/friends/import/steam" element={<SteamImportPage />} />
            <Route path="/u/:username" element={<ProfilePage />} />
            <Route path="/add/:code" element={<AddFriendPage />} />
            <Route path="/settings/profile" element={<ProfileSettingsPage />} />
            <Route path="/settings/account" element={<AccountSettingsPage />} />
            <Route path="/settings/blocked" element={<BlockedUsersPage />} />
          </Route>
        </Route>
      </Route>

      <Route path="*" element={<Navigate replace to="/welcome" />} />
    </Routes>
  )
}

export default App
