/**
 * TOUR_DESIGN.md §2: eight stops, nav order, anchored to `data-tour` attributes on the shipped
 * chrome elements (`components/shell/TopBar.tsx`/`BottomTabBar.tsx`'s nav items,
 * `NotificationBell.tsx`'s `NavLink`, `AccountMenu.tsx`'s trigger). The `id` doubles as the i18n
 * key segment (`tour:steps.<id>.name`/`.body`) and the `data-tour` value every anchor carries.
 * <p>
 * S2: the "discover" anchor moved off the persistent nav onto Home's discover-section link
 * (`HomePage.tsx`) — safe because `TourOverlay` only ever opens while `location.pathname === '/app'`
 * (its own `onAppRoute` gate), and `/app` renders exactly that page, so the anchor always exists
 * whenever a step could reach it. `TourOverlay`'s missing-anchor fallback (centered card, no crash)
 * is therefore unreachable for this anchor in practice, not a case this design leans on.
 */
export const TOUR_STEP_IDS = [
  'home',
  'friends',
  'sessions',
  'polls',
  'calendar',
  'discover',
  'notifications',
  'settings',
] as const

export type TourStepId = (typeof TOUR_STEP_IDS)[number]

export const TOUR_STEP_COUNT = TOUR_STEP_IDS.length
