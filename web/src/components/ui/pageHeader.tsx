import { useEffect, useRef, type ReactNode } from 'react'

/**
 * The page's h1 plus up to two header actions and an optional subtitle/meta row
 * (STYLING_OVERHAUL_DESIGN.md §3.1). The h1 carries the shipped focus-on-mount
 * contract the `focuses the page h1 on mount_D139` facts assert: programmatic
 * focus target (`tabIndex={-1}`), no resting focus ring (`focus:outline-none` —
 * the global `:focus-visible` rule still paints keyboard focus).
 *
 * `live` is opt-in: only pages whose h1 text changes in place under the viewer
 * (rather than on navigation) announce it, so no page gains a second competing
 * announcement source.
 */
export interface PageHeaderProps {
  title: ReactNode
  id?: string
  actions?: ReactNode
  subtitle?: ReactNode
  meta?: ReactNode
  live?: boolean
  /** 'center' keeps the column centered at every width (auth/onboarding cards). */
  align?: 'start' | 'center'
  className?: string
}

export function PageHeader({
  title,
  id,
  actions,
  subtitle,
  meta,
  live = false,
  align = 'start',
  className,
}: PageHeaderProps) {
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const centered = align === 'center'
  const classes = [
    'flex flex-col gap-3',
    centered && 'items-center text-center',
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <div className={classes}>
      <div
        className={
          centered
            ? 'flex flex-col items-center gap-3'
            : 'flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between'
        }
      >
        <h1
          ref={heading}
          aria-live={live ? 'polite' : undefined}
          className="font-display text-display font-bold text-ink focus:outline-none"
          id={id}
          tabIndex={-1}
        >
          {title}
        </h1>
        {actions && (
          <div className="flex flex-wrap items-center gap-2 sm:flex-none">{actions}</div>
        )}
      </div>
      {subtitle && <p className="text-ink-soft">{subtitle}</p>}
      {meta && (
        <div
          className={`flex flex-wrap items-center gap-2 text-meta text-ink-soft${centered ? ' justify-center' : ''}`}
        >
          {meta}
        </div>
      )}
    </div>
  )
}
