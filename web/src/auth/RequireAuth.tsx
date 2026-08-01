import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { useSession } from './useSession'

type RequireAuthProps = {
  children?: ReactNode
}

export function RequireAuth({ children }: RequireAuthProps) {
  const location = useLocation()
  const { data: session, isPending } = useSession()

  if (isPending) {
    return null
  }

  if (!session?.authenticated) {
    const next = `${location.pathname}${location.search}${location.hash}`
    return <Navigate replace to={`/login?next=${encodeURIComponent(next)}`} />
  }

  return children ?? <Outlet />
}
