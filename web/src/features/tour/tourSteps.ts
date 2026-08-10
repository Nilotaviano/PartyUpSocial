/**
 * TOUR_DESIGN.md §2: eight stops, nav order, anchored to `data-tour` attributes on the shipped
 * chrome elements (`App.tsx`'s `AuthenticatedShell` nav, `NotificationBell.tsx`'s `NavLink`). The
 * `id` doubles as the i18n key segment (`tour:steps.<id>.name`/`.body`) and the `data-tour` value
 * every anchor element carries.
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
