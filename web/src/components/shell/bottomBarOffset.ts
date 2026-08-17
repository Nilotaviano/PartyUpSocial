/**
 * Positioning for a page-level fixed bottom bar (action bars, footers).
 *
 * `BottomTabBar` is itself `fixed bottom-0 z-40 lg:hidden` with a 4rem row plus
 * the safe-area inset, and it renders after page content, so a page bar pinned
 * at `bottom-0` is painted over on mobile. The shell's `pb-` clearance only
 * protects in-flow content, never fixed descendants — each such bar has to
 * lift itself by the tab bar's height and drop back to the edge at `lg`.
 */
export const BOTTOM_BAR_OFFSET_CLASSES = 'bottom-[calc(4rem+env(safe-area-inset-bottom))] lg:bottom-0'
