import { CalendarDays, Gamepad2, Home, Users, Vote } from 'lucide-react'
import type { ComponentType } from 'react'

/**
 * The five chrome-level sections (owner ruling: Discover lives on the Home
 * page, not its own nav entry). `end` follows react-router `NavLink`
 * semantics for the common case; Sessions, Friends, and Calendar are items
 * whose link target is narrower than their active range (each has live
 * sub-routes — `/sessions/*`, `/friends/import/steam`, `/calendar/agenda`),
 * so their active state is computed from `matchPrefix` rather than from
 * `to`/`end` alone. Home stays exact: `/app` has no sub-routes.
 */
export interface NavItem {
  to: string
  labelKey: string
  ns: string
  icon: ComponentType<{ className?: string; 'aria-hidden'?: boolean }>
  end: boolean
  matchPrefix: string
  /** D-182 tour anchor id (`TOUR_STEP_IDS`) — carried by both TopBar's and
   *  BottomTabBar's rendering of this item; `TourOverlay` resolves whichever
   *  instance is actually visible (see its anchor-resolution note). */
  tourId: string
}

export const NAV_ITEMS: NavItem[] = [
  { to: '/app', labelKey: 'navigation.home', ns: 'common', icon: Home, end: true, matchPrefix: '/app', tourId: 'home' },
  {
    to: '/friends',
    labelKey: 'navigation.friends',
    ns: 'common',
    icon: Users,
    end: false,
    matchPrefix: '/friends',
    tourId: 'friends',
  },
  {
    to: '/sessions/upcoming',
    labelKey: 'nav.sessions',
    ns: 'sessions',
    icon: Gamepad2,
    end: false,
    matchPrefix: '/sessions',
    tourId: 'sessions',
  },
  {
    to: '/polls',
    labelKey: 'nav.polls',
    ns: 'polls',
    icon: Vote,
    end: false,
    matchPrefix: '/polls',
    tourId: 'polls',
  },
  {
    to: '/calendar',
    labelKey: 'nav.calendar',
    ns: 'availability',
    icon: CalendarDays,
    end: false,
    matchPrefix: '/calendar',
    tourId: 'calendar',
  },
]

/** Prefix-aware active check — see `NavItem.matchPrefix` above. */
export function isNavItemActive(pathname: string, item: NavItem): boolean {
  if (item.end) return pathname === item.matchPrefix
  return pathname === item.matchPrefix || pathname.startsWith(`${item.matchPrefix}/`)
}
