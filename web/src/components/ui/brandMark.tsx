/**
 * The brand mark (STYLING_OVERHAUL_DESIGN.md §3.6): two offset rounded
 * time-blocks overlapping into one filled cell — the calendar-block motif the
 * product is built on, reduced to a nav-scale figure.
 *
 * Constraint: the tomato fill is the 5.1 brand-moment reservation and is the
 * same value in both themes. Recomputed for §9 R4 — #E4572E against the dark
 * page #161d10 is 4.68:1 and against the light page #f6f2e3 is 3.29:1, both
 * over the 3:1 non-text floor, so the accent fallback (#ffd447) specified for a
 * failing recomputation is not needed and is deliberately not shipped.
 *
 * The outline is `currentColor`, so the mark inherits ink from whatever it sits
 * beside and stays correct in both themes without a class of its own. It is
 * `aria-hidden` decoration: the wordmark link keeps its own accessible name.
 */
const BRAND_FILL = '#E4572E'

export function BrandMark({ className = 'h-6 w-6' }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      className={className}
      fill="none"
      focusable="false"
      viewBox="0 0 24 24"
      xmlns="http://www.w3.org/2000/svg"
    >
      <rect
        fill={BRAND_FILL}
        height="10"
        rx="2.5"
        stroke="currentColor"
        strokeWidth="2"
        width="12"
        x="2"
        y="3"
      />
      <rect
        fill={BRAND_FILL}
        height="10"
        rx="2.5"
        stroke="currentColor"
        strokeWidth="2"
        width="12"
        x="10"
        y="11"
      />
    </svg>
  )
}
