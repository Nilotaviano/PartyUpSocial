import type { ReactNode } from 'react'

/**
 * Empty states (STYLING_OVERHAUL_DESIGN.md §3.6): illustration, a
 * product-voice sentence, and a way out. `variant="quiet"` drops the CTA and is
 * for empties that are a *good* terminal state (an empty inbox, an empty
 * blocked list); everywhere else the CTA is required — the union below makes
 * omitting it a type error rather than a review note.
 *
 * The illustration slot is emoji-scale decoration: it renders inside an
 * `aria-hidden` span, so it never enters the accessible name of anything.
 */
interface EmptyStateBaseProps {
  children: ReactNode
  illustration?: ReactNode
  title?: ReactNode
  className?: string
  'data-testid'?: string
}

export type EmptyStateProps = EmptyStateBaseProps &
  ({ variant?: 'default'; action: ReactNode } | { variant: 'quiet'; action?: never })

export function EmptyState({
  children,
  illustration,
  title,
  action,
  className,
  'data-testid': testId,
}: EmptyStateProps) {
  const classes = [
    'flex flex-col items-center gap-4 rounded-card border-2 border-dashed border-ink/25 px-6 py-10 text-center',
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <div className={classes} data-testid={testId}>
      {illustration && (
        <span aria-hidden="true" className="text-4xl leading-none">
          {illustration}
        </span>
      )}
      {title && <h3 className="font-display text-h3 font-semibold text-ink">{title}</h3>}
      <p className="max-w-prose font-body text-body text-ink-soft">{children}</p>
      {action}
    </div>
  )
}
