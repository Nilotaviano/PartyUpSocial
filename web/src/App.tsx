import { Navigate, Route, Routes } from 'react-router'
import { useTranslation } from 'react-i18next'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'

type PlaceholderProps = {
  testId: string
  translationKey: string
}

function Placeholder({ testId, translationKey }: PlaceholderProps) {
  const { t } = useTranslation(['common', 'auth', 'onboarding', 'profile', 'settings', 'errors'])

  return (
    <main data-testid={testId}>
      <h1>{t(translationKey)}</h1>
    </main>
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

  if (session.state === 'Onboarding' || session.onboarding !== null) {
    return <Navigate replace to="/onboarding" />
  }

  return <Navigate replace to="/app" />
}

function App() {
  return (
    <Routes>
      <Route path="/" element={<LifecycleRedirect />} />
      <Route path="/welcome" element={<Placeholder testId="screen-welcome" translationKey="pages.welcome" />} />
      <Route path="/age-gate" element={<Placeholder testId="screen-age-gate" translationKey="pages.ageGate" />} />
      <Route path="/login" element={<Placeholder testId="screen-login" translationKey="auth:pages.login" />} />
      <Route path="/register" element={<Placeholder testId="screen-register" translationKey="auth:pages.register" />} />
      <Route path="/auth/magic" element={<Placeholder testId="screen-auth-magic" translationKey="auth:pages.magic" />} />
      <Route path="/auth/callback" element={<Placeholder testId="screen-auth-callback" translationKey="auth:pages.callback" />} />
      <Route path="/auth/reset" element={<Placeholder testId="screen-auth-reset" translationKey="auth:pages.reset" />} />
      <Route path="/auth/confirm-identity" element={<Placeholder testId="screen-auth-confirm-identity" translationKey="auth:pages.confirmIdentity" />} />
      <Route path="/auth/verify-email" element={<Placeholder testId="screen-auth-verify-email" translationKey="auth:pages.verifyEmail" />} />
      <Route path="/auth/email-change" element={<Placeholder testId="screen-auth-email-change" translationKey="auth:pages.emailChange" />} />
      <Route path="/account-deletion" element={<Placeholder testId="screen-account-deletion" translationKey="pages.accountDeletion" />} />
      <Route path="/account-deletion/confirm" element={<Placeholder testId="screen-account-deletion-confirm" translationKey="pages.accountDeletionConfirm" />} />

      <Route element={<RequireAuth />}>
        <Route path="/onboarding" element={<Placeholder testId="screen-onboarding" translationKey="onboarding:pages.root" />} />
        <Route path="/onboarding/username" element={<Placeholder testId="screen-onboarding-username" translationKey="onboarding:pages.username" />} />
        <Route path="/onboarding/display-name" element={<Placeholder testId="screen-onboarding-display-name" translationKey="onboarding:pages.displayName" />} />
        <Route path="/onboarding/avatar" element={<Placeholder testId="screen-onboarding-avatar" translationKey="onboarding:pages.avatar" />} />
        <Route path="/onboarding/time-zone" element={<Placeholder testId="screen-onboarding-time-zone" translationKey="onboarding:pages.timeZone" />} />
        <Route path="/onboarding/link-platforms" element={<Placeholder testId="screen-onboarding-link-platforms" translationKey="onboarding:pages.linkPlatforms" />} />
        <Route path="/onboarding/friends" element={<Placeholder testId="screen-onboarding-friends" translationKey="onboarding:pages.friends" />} />

        <Route element={<RequireOnboarded />}>
          <Route path="/app" element={<Placeholder testId="screen-app" translationKey="pages.app" />} />
          <Route path="/settings/profile" element={<Placeholder testId="screen-settings-profile" translationKey="profile:pages.profile" />} />
          <Route path="/settings/account" element={<Placeholder testId="screen-settings-account" translationKey="settings:pages.account" />} />
        </Route>
      </Route>

      <Route path="*" element={<Navigate replace to="/welcome" />} />
    </Routes>
  )
}

export default App
