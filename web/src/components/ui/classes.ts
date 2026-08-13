// Shared class recipes for hand-rolled interactive elements that can't use
// <Button> (e.g. router <Link>s styled as buttons). Mirrors the "raised"
// look ui/button.tsx ships (2px ink border, radius-control, offset shadow,
// 80ms press) per PHASE5_1_DESIGN.md §3 — callers layer their own
// flex/padding/color classes alongside this recipe.
export const raisedInteractiveClasses =
  'min-h-11 rounded-control border-2 border-ink font-display font-semibold shadow-offset-control motion-safe:transition-transform motion-safe:motion-press motion-safe:ease-out active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

// Native field skins (STYLING_OVERHAUL_DESIGN.md §3.2) — skin only, on top of
// the shipped DOM. `control-frame`/`control-select` are the CSS recipes in
// index.css; these constants add the layout the form fields share.
export const fieldControlClasses =
  'control-frame min-h-11 w-full px-3 py-2 font-body text-body placeholder:text-ink-soft disabled:bg-busy disabled:text-ink-soft disabled:opacity-100'

export const fieldSelectClasses = `${fieldControlClasses} control-select`

// A wrapping-label native control (checkbox/radio) keeps its exact structure
// (§3.2) and gets its row frame here: the whole row is the hit target, ≥44px.
export const controlRowClasses =
  'control-frame flex min-h-11 w-full items-center gap-3 px-3 py-2 font-body text-body'

// Chips (STYLING_OVERHAUL_DESIGN.md §3.4). Both carry their meaning as text —
// never colour alone — so a chip stays legible with colour stripped away.
// `chipClasses` is the emphatic accent chip (status, in-progress);
// `quietChipClasses` is the meta chip a page's facts (date, platform, game)
// sit in on the page background.
export const chipClasses =
  'inline-flex items-center rounded-full border-2 border-ink bg-accent px-2 py-0.5 font-display text-xs font-bold text-accent-ink dark:border-accent-ink'

export const quietChipClasses =
  'inline-flex items-center rounded-full border-2 border-ink/25 bg-card px-3 py-1 font-body text-meta text-ink'

// SegmentStrip (STYLING_OVERHAUL_DESIGN.md §3.2) — container chrome and the
// item skin live together because they are one recipe: the container rounds
// the outer corners and collapses the doubled inner borders of whatever the
// caller renders, so the item classes must stay square-cornered.
export const segmentStripClasses = [
  'flex w-full flex-col sm:w-auto sm:flex-row',
  '[&>*]:rounded-none',
  '[&>*+*]:border-t-0 sm:[&>*+*]:border-t-2 sm:[&>*+*]:border-l-0',
  'max-sm:[&>*:first-child]:rounded-t-control max-sm:[&>*:last-child]:rounded-b-control',
  'sm:[&>*:first-child]:rounded-l-control sm:[&>*:last-child]:rounded-r-control',
].join(' ')

const segmentItemBaseClasses =
  'inline-flex min-h-11 flex-1 items-center justify-center gap-2 border-2 border-ink px-4 py-2 font-display font-semibold motion-safe:transition-transform motion-safe:motion-press motion-safe:ease-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50'

/**
 * The SegmentStrip item skin, applied by the caller to its own button — the
 * caller keeps ownership of `aria-pressed`/`aria-current` and the handler.
 */
export function segmentItemClasses(selected: boolean, className?: string): string {
  return [
    segmentItemBaseClasses,
    selected ? 'bg-primary text-primary-ink' : 'bg-card text-ink',
    className,
  ]
    .filter(Boolean)
    .join(' ')
}
