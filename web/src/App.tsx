import type { ReactNode } from 'react'
import { Navigate, Outlet, Route, Routes } from 'react-router'
import { RequireAuth } from './auth/RequireAuth'
import { RequireOnboarded } from './auth/RequireOnboarded'
import { useSession } from './auth/useSession'
import { AppFooter } from './components/AppFooter'
import { BottomTabBar } from './components/shell/BottomTabBar'
import { TopBar } from './components/shell/TopBar'
import { PageSlotProvider } from './components/ui/pageScaffold'
import { ToastHost } from './components/ui/toastHost'
import { AdminDashboardPage } from './features/admin/AdminDashboardPage'
import { AdminUserDetailPage } from './features/admin/AdminUserDetailPage'
import { AdminUsersPage } from './features/admin/AdminUsersPage'
import {
  AgeGate,
  AuthCallback,
  ConfirmIdentity,
  EmailChange,
  ForgotPassword,
  Login,
  MagicLink,
  Register,
  ResetPassword,
  VerifyEmail,
  Welcome,
} from './features/auth/AuthScreens'
import { FindATimePage } from './features/availability/FindATimePage'
import { CalendarShell } from './features/availability/lovable/CalendarShell'
import { DiscoverPage } from './features/discover/DiscoverPage'
import { AddFriendPage } from './features/friends/AddFriendPage'
import { FriendsPage } from './features/friends/FriendsPage'
import { ProfilePage } from './features/friends/ProfilePage'
import { SteamImportPage } from './features/friends/SteamImportPage'
import { HomePage } from './features/home/HomePage'
import { CommunityStandardsPage } from './features/legal/CommunityStandardsPage'
import { PrivacyPolicyPage } from './features/legal/PrivacyPolicyPage'
import { TermsPage } from './features/legal/TermsPage'
import { NotificationCenterPage } from './features/notifications/NotificationCenterPage'
import { PushEnableCard } from './features/notifications/PushEnableCard'
import { OnboardingWizard } from './features/onboarding/OnboardingWizard'
import { TourOverlay } from './features/tour'
import { PublicDeletionConfirmPage } from './features/deletion/PublicDeletionConfirmPage'
import { PublicDeletionRequestPage } from './features/deletion/PublicDeletionRequestPage'
import { PollInvitePage } from './features/polls/PollInvitePage'
import { PollCreatePage } from './features/polls/lovable/PollCreatePage'
import { PollDetailPage } from './features/polls/lovable/PollDetailPage'
import { PollListPage } from './features/polls/lovable/PollListPage'
import { ProfileSettingsPage } from './features/profile/ProfileSettingsPage'
import { AccountSettingsPage } from './features/settings/AccountSettingsPage'
import { BlockedUsersPage } from './features/settings/BlockedUsersPage'
import { EmailUnsubscribePage } from './features/settings/EmailUnsubscribePage'
import { NotificationSettingsPage } from './features/settings/NotificationSettingsPage'
import { GuestSessionPage } from './features/sessions/GuestSessionPage'
import { SessionInvitePage } from './features/sessions/SessionInvitePage'
import { SessionCreatePage } from './features/sessions/lovable/SessionCreatePage'
import { SessionDetailPage } from './features/sessions/lovable/SessionDetailPage'
import { SessionEditPage } from './features/sessions/lovable/SessionEditPage'
import { SessionListPage } from './features/sessions/lovable/SessionListPage'

/**
 * Shell content wrapper: a `<div>`, deliberately not a `<main>`. Every routed
 * page under this shell already renders its own single `<main>` landmark
 * (`pageScaffold.tsx`'s own contract: "Shells own the chrome and pin the
 * footer; pages keep their own `<main>`") — wrapping `Outlet` in a second one
 * here would nest two `main` landmarks, which HTML disallows.
 */
function ShellContent({ children }: { children: ReactNode }) {
  return <div className="flex w-full flex-1 flex-col">{children}</div>
}

function AuthenticatedShell() {
  return (
    <div className="flex min-h-dvh flex-col bg-page text-ink pb-[calc(4rem+env(safe-area-inset-bottom))] lg:pb-0">
      <TopBar />
      <ToastHost>
        <PageSlotProvider>
          <PushEnableCard />
          <TourOverlay />
          <ShellContent>
            <Outlet />
          </ShellContent>
        </PageSlotProvider>
      </ToastHost>
      <AppFooter />
      <BottomTabBar />
    </div>
  )
}

/**
 * The logged-out surfaces D-072 addresses (IMPLEMENTATION_PLAN.md:146): the
 * welcome/sign-in screens, the public deletion pages and the legal documents
 * themselves all carry the footer, so a logged-out user always has a route to
 * the deletion request and to the policies.
 */
function PublicShell() {
  return (
    <div className="flex min-h-dvh flex-col bg-page text-ink">
      <div className="flex flex-1 flex-col">
        <Outlet />
      </div>
      <AppFooter />
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
      <Route element={<PublicShell />}>
        <Route path="/welcome" element={<Welcome />} />
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route path="/forgot-password" element={<ForgotPassword />} />
        <Route path="/account-deletion" element={<PublicDeletionRequestPage />} />
        <Route
          path="/account-deletion/confirm"
          element={<PublicDeletionConfirmPage />}
        />
        <Route path="/privacy" element={<PrivacyPolicyPage />} />
        <Route path="/terms" element={<TermsPage />} />
        <Route path="/community-standards" element={<CommunityStandardsPage />} />
      </Route>

      <Route path="/age-gate" element={<AgeGate />} />
      <Route path="/auth/magic" element={<MagicLink />} />
      <Route path="/auth/callback" element={<AuthCallback />} />
      <Route path="/auth/reset" element={<ResetPassword />} />
      <Route path="/auth/confirm-identity" element={<ConfirmIdentity />} />
      <Route path="/auth/verify-email" element={<VerifyEmail />} />
      <Route path="/auth/email-change" element={<EmailChange />} />
      <Route path="/email-unsubscribe" element={<EmailUnsubscribePage />} />

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
            <Route path="/calendar" element={<CalendarShell />} />
            <Route path="/calendar/agenda" element={<CalendarShell />} />
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
            <Route path="/polls/:pollId/invite" element={<PollInvitePage />} />

            {/*
              PHASE8_DESIGN.md §2 Q2 (ratified 2026-08-12): the admin surface is
              reached by URL only — no navigation entry is rendered for anyone,
              here or in AuthenticatedShell above. D-177's 404 posture makes nav
              gating unnecessary: a non-admin who guesses these URLs gets the
              page-local not-found branch each page renders for a 404, which
              names nothing admin-specific.
            */}
            <Route path="/admin" element={<AdminDashboardPage />} />
            <Route path="/admin/users" element={<AdminUsersPage />} />
            <Route path="/admin/users/:userId" element={<AdminUserDetailPage />} />
          </Route>
        </Route>
      </Route>

      <Route path="*" element={<Navigate replace to="/welcome" />} />
    </Routes>
  )
}

export default App
