import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import {
  validateAuthNext,
  validateFriendContinuation,
  withFriendContinuation,
} from './friendContinuation'
import { useSession } from './useSession'

type RequireOnboardedProps = {
  children?: ReactNode
}

function needsOnboarding(state: string | null, hasOnboarding: boolean) {
  return state === 'onboarding' || hasOnboarding
}

export function RequireOnboarded({ children }: RequireOnboardedProps) {
  const location = useLocation()
  const { data: session, isPending } = useSession()

  if (isPending) {
    return null
  }

  if (!session?.authenticated) {
    const next = validateAuthNext(location.pathname) ?? '/app'
    return <Navigate replace to={`/login?next=${encodeURIComponent(next)}`} />
  }

  if (needsOnboarding(session.state, session.onboarding !== null)) {
    const continuation = validateFriendContinuation(location.pathname)
    return (
      <Navigate
        replace
        to={withFriendContinuation('/onboarding', continuation)}
      />
    )
  }

  return children ?? <Outlet />
}
