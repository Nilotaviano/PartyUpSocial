import type { ReactNode } from 'react'
import { segmentStripClasses } from './classes'

/**
 * Connected chip group (STYLING_OVERHAUL_DESIGN.md §3.2) — **visual only**.
 *
 * It manages no roles, no focus, no selection: callers keep their shipped
 * semantics (RSVP's `aria-pressed` buttons inside a `role=group`, the theme and
 * filter strips likewise) and only borrow the look, applying
 * `segmentItemClasses` to their own buttons. It is deliberately never a
 * radiogroup — D-084's arrow-key hazard is why RSVP is a pressed-button group
 * in the first place, and a presentational wrapper cannot reintroduce it.
 *
 * Corners and the doubled inner borders are handled by the container over its
 * own children, so an item never has to know its position in the strip. Below
 * `sm` the strip stacks into full-width 44px rows.
 */
export interface SegmentStripProps {
  children: ReactNode
  className?: string
}

export function SegmentStrip({ children, className }: SegmentStripProps) {
  return (
    <div className={[segmentStripClasses, className].filter(Boolean).join(' ')}>{children}</div>
  )
}
