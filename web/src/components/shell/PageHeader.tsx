import { useEffect, useRef, type ReactNode } from 'react'
import { ChevronLeft } from 'lucide-react'
import { Link } from 'react-router'

export interface ShellPageHeaderBack {
  to: string
  label: string
}

/**
 * Shell page header: optional back affordance, eyebrow, title, supporting
 * line and contextual actions — merged with the production focus-on-mount
 * contract the `focuses the page h1 on mount_D139` facts assert:
 * programmatic focus target (`tabIndex={-1}`), no resting focus ring
 * (`focus:outline-none` — the global `:focus-visible` rule still paints
 * keyboard focus). `live` is opt-in, same rationale as `ui/pageHeader.tsx`.
 */
export interface ShellPageHeaderProps {
  title: ReactNode
  eyebrow?: string
  description?: ReactNode
  actions?: ReactNode
  back?: ShellPageHeaderBack
  live?: boolean
  className?: string
}

export function PageHeader({
  title,
  eyebrow,
  description,
  actions,
  back,
  live = false,
  className,
}: ShellPageHeaderProps) {
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  return (
    <header
      className={['mx-auto w-full max-w-[1400px] px-3 pt-4 pb-3 sm:px-6', className].filter(Boolean).join(' ')}
    >
      {back && (
        <Link
          className="mb-2 -ml-2 inline-flex h-11 items-center gap-1 rounded-xl px-2 font-display text-[13px] font-bold text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          to={back.to}
        >
          <ChevronLeft aria-hidden className="size-4" />
          {back.label}
        </Link>
      )}
      <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
        <div className="min-w-0">
          {eyebrow && (
            <p className="font-display text-[11px] font-extrabold tracking-[0.14em] text-muted-foreground uppercase">
              {eyebrow}
            </p>
          )}
          <h1
            ref={heading}
            aria-live={live ? 'polite' : undefined}
            className="mt-0.5 font-display text-[26px] leading-tight font-extrabold focus:outline-none sm:text-3xl"
            tabIndex={-1}
          >
            {title}
          </h1>
          {description && <p className="mt-1 max-w-prose text-[13px] text-muted-foreground">{description}</p>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2 sm:justify-end">{actions}</div>}
      </div>
    </header>
  )
}
