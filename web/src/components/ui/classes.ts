// Shared class recipes for hand-rolled interactive elements that can't use
// <Button> (e.g. router <Link>s styled as buttons). Mirrors the "raised"
// look ui/button.tsx ships (2px ink border, radius-control, offset shadow,
// 80ms press) per PHASE5_1_DESIGN.md §3 — callers layer their own
// flex/padding/color classes alongside this recipe.
export const raisedInteractiveClasses =
  'min-h-11 rounded-control border-2 border-ink font-display font-semibold shadow-offset-control motion-safe:transition-transform motion-safe:duration-75 motion-safe:ease-out active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
