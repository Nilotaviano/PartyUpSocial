import type { ComponentType, ReactNode } from 'react'

/**
 * Presentational one-line notice strip (prototype grammar) with a quiet
 * `status` variant for the D-175 push fallback. No push (or any other)
 * logic lives here — callers wire actions through props.
 *
 * Deviation from the prototype: the prototype's dismiss is an icon-only "X"
 * with an aria-label. `PushEnableCard`'s browser tests match a visible
 * "Not now" button by accessible name, so `dismissLabel` renders as a
 * labelled text button here instead — shaped to what that caller needs.
 */
export interface NoticeBannerProps {
  icon?: ComponentType<{ className?: string; 'aria-hidden'?: boolean }>
  title: ReactNode
  body?: ReactNode
  actionLabel?: string
  onAction?: () => void
  actionBusy?: boolean
  dismissLabel?: string
  onDismiss?: () => void
  variant?: 'prompt' | 'status'
  error?: ReactNode
  className?: string
  'data-testid'?: string
}

export function NoticeBanner({
  icon: Icon,
  title,
  body,
  actionLabel,
  onAction,
  actionBusy = false,
  dismissLabel,
  onDismiss,
  variant = 'prompt',
  error,
  className,
  'data-testid': testId,
}: NoticeBannerProps) {
  if (variant === 'status') {
    return (
      <p className={className ?? 'text-[13px] text-muted-foreground'} role="status">
        {title}
      </p>
    )
  }

  return (
    <div className={className} data-testid={testId}>
      <div className="flex flex-wrap items-center gap-3 rounded-xl border border-border border-l-4 border-l-accent bg-card px-3 py-2 shadow-[var(--shadow-card)]">
        {Icon && <Icon aria-hidden className="size-4 shrink-0 text-accent-foreground" />}
        <p className="min-w-0 flex-1 text-[12.5px] leading-snug">
          <span className="font-display font-bold">{title}</span>
          {body && (
            <>
              {' '}
              <span className="text-muted-foreground">{body}</span>
            </>
          )}
        </p>
        <div className="flex shrink-0 items-center gap-2">
          {actionLabel && onAction && (
            <button
              aria-busy={actionBusy}
              className="inline-flex h-9 items-center justify-center rounded-lg bg-primary px-3 font-display text-[12.5px] font-bold text-primary-foreground shadow-[var(--shadow-card)] hover:opacity-90 disabled:pointer-events-none disabled:opacity-50"
              disabled={actionBusy}
              type="button"
              onClick={onAction}
            >
              {actionLabel}
            </button>
          )}
          {dismissLabel && onDismiss && (
            <button
              className="inline-flex h-9 items-center justify-center rounded-lg px-3 font-display text-[12.5px] font-bold text-muted-foreground hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-50"
              disabled={actionBusy}
              type="button"
              onClick={onDismiss}
            >
              {dismissLabel}
            </button>
          )}
        </div>
      </div>
      {error && (
        <p className="mt-1 pl-1 text-[12.5px] text-destructive" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
