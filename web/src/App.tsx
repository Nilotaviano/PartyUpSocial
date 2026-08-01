import { Navigate, Route, Routes } from 'react-router'
import { useTranslation } from 'react-i18next'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'
import { AgeGate, AuthCallback, ConfirmIdentity, EmailChange, Login, MagicLink, Register, ResetPassword, VerifyEmail, Welcome } from './features/auth/AuthScreens'
import { OnboardingWizard } from './features/onboarding/OnboardingWizard'

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
      <Route path="/account-deletion" element={<Placeholder testId="screen-account-deletion" translationKey="pages.accountDeletion" />} />
      <Route path="/account-deletion/confirm" element={<Placeholder testId="screen-account-deletion-confirm" translationKey="pages.accountDeletionConfirm" />} />

      <Route element={<RequireAuth />}>
        <Route path="/onboarding" element={<OnboardingWizard />} />
        <Route path="/onboarding/username" element={<OnboardingWizard />} />
        <Route path="/onboarding/display-name" element={<OnboardingWizard />} />
        <Route path="/onboarding/avatar" element={<OnboardingWizard />} />
        <Route path="/onboarding/time-zone" element={<OnboardingWizard />} />
        <Route path="/onboarding/link-platforms" element={<OnboardingWizard />} />
        <Route path="/onboarding/friends" element={<OnboardingWizard />} />

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
