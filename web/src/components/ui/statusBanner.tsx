import type { ReactNode } from 'react'

/**
 * Persistent, page-scoped state (STYLING_OVERHAUL_DESIGN.md §3.5): form and
 * query errors, accepted-request confirmations, the push prompt. Transient
 * confirmations are toasts instead.
 *
 * Live-region rule: only `children` — the message text — sits inside the
 * announced element. The chrome (border, glyph, dismiss, actions) is a sibling
 * of it, so a page that already owns a bound `p[aria-live]` keeps exactly one
 * announcement source and never announces its own buttons.
 */
export type StatusBannerVariant = 'info' | 'success' | 'danger'

interface StatusBannerBaseProps {
  children: ReactNode
  variant?: StatusBannerVariant
  live?: 'polite' | 'assertive'
  role?: 'status' | 'alert'
  actions?: ReactNode
  className?: string
  'data-testid'?: string
}

export type StatusBannerProps = StatusBannerBaseProps &
  ({ onDismiss?: never; dismissLabel?: never } | { onDismiss: () => void; dismissLabel: string })

const variantClasses: Record<StatusBannerVariant, string> = {
  info: 'border-ink bg-accent text-accent-ink dark:border-accent-ink',
  success: 'border-ink bg-primary text-primary-ink dark:border-primary-ink',
  danger: 'border-ink bg-danger text-danger-ink dark:border-danger-ink',
}

const variantGlyph: Record<StatusBannerVariant, string> = {
  info: 'ⓘ',
  success: '✓',
  danger: '!',
}

const dismissClasses =
  'inline-flex min-h-11 min-w-11 flex-none items-center justify-center rounded-control text-current focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

export function StatusBanner({
  children,
  variant = 'info',
  live,
  role,
  actions,
  onDismiss,
  dismissLabel,
  className,
  'data-testid': testId,
}: StatusBannerProps) {
  const classes = [
    'flex flex-wrap items-center gap-3 rounded-control border-2 px-4 py-2 font-body',
    variantClasses[variant],
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <div className={classes} data-testid={testId}>
      <span aria-hidden="true" className="font-display font-bold">
        {variantGlyph[variant]}
      </span>
      <p aria-live={live} className="min-w-0 flex-1" role={role}>
        {children}
      </p>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      {onDismiss && (
        <button
          aria-label={dismissLabel}
          className={dismissClasses}
          type="button"
          onClick={onDismiss}
        >
          <span aria-hidden="true">✕</span>
        </button>
      )}
    </div>
  )
}
