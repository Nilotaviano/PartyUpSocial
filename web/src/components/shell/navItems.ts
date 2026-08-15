import { CalendarDays, Gamepad2, Home, Users, Vote } from 'lucide-react'
import type { ComponentType } from 'react'

/**
 * The five chrome-level sections (owner ruling: no Discover in nav — it
 * moves to the Home page in a later slice). `end` follows react-router
 * `NavLink` semantics for the common case; Sessions is the one item whose
 * link target (`/sessions/upcoming`) is narrower than its active range
 * (`/sessions/*` — detail/create/history pages too), so its active state is
 * computed from `matchPrefix` rather than from `to`/`end` alone.
 */
export interface NavItem {
  to: string
  labelKey: string
  ns: string
  icon: ComponentType<{ className?: string; 'aria-hidden'?: boolean }>
  end: boolean
  matchPrefix: string
}

export const NAV_ITEMS: NavItem[] = [
  { to: '/app', labelKey: 'navigation.home', ns: 'common', icon: Home, end: true, matchPrefix: '/app' },
  { to: '/friends', labelKey: 'navigation.friends', ns: 'common', icon: Users, end: true, matchPrefix: '/friends' },
  {
    to: '/sessions/upcoming',
    labelKey: 'nav.sessions',
    ns: 'sessions',
    icon: Gamepad2,
    end: false,
    matchPrefix: '/sessions',
  },
  { to: '/polls', labelKey: 'nav.polls', ns: 'polls', icon: Vote, end: false, matchPrefix: '/polls' },
  {
    to: '/calendar',
    labelKey: 'nav.calendar',
    ns: 'availability',
    icon: CalendarDays,
    end: true,
    matchPrefix: '/calendar',
  },
]

/** Prefix-aware active check — see `NavItem.matchPrefix` above. */
export function isNavItemActive(pathname: string, item: NavItem): boolean {
  if (item.end) return pathname === item.matchPrefix
  return pathname === item.matchPrefix || pathname.startsWith(`${item.matchPrefix}/`)
}
