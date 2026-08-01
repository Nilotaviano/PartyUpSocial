import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { useSession } from './useSession'

type RequireOnboardedProps = {
  children?: ReactNode
}

function needsOnboarding(state: string | null, hasOnboarding: boolean) {
  return state === 'Onboarding' || hasOnboarding
}

export function RequireOnboarded({ children }: RequireOnboardedProps) {
  const location = useLocation()
  const { data: session, isPending } = useSession()

  if (isPending) {
    return null
  }

  if (!session?.authenticated) {
    const next = `${location.pathname}${location.search}${location.hash}`
    return <Navigate replace to={`/login?next=${encodeURIComponent(next)}`} />
  }

  if (needsOnboarding(session.state, session.onboarding !== null)) {
    return <Navigate replace to="/onboarding" />
  }

  return children ?? <Outlet />
}
