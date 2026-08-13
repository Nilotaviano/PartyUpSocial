import { useId, type ReactNode } from 'react'

/**
 * Page groupings (STYLING_OVERHAUL_DESIGN.md §3.1).
 *
 * `Section` is the default: a heading and `gap-6` of content, no border.
 * `SectionCard` (ink outline + offset shadow) is reserved for a record of a
 * thing that exists independently of the page — a share-code object, the
 * viewer's own RSVP block — or for a hazard boundary (danger zone). Triage and
 * attention groupings are Sections, not cards. A bordered card never contains
 * another bordered card, and a page carries at most three SectionCards.
 *
 * Repeated person/session rows are hairline rows (`border-t border-ink/25`)
 * inside one Section, never one card per row — the calendar summary panel's
 * per-rule card rows are the ratified exception (CALENDAR_DESIGN.md §9 S4).
 */
export interface SectionProps {
  children: ReactNode
  title?: ReactNode
  titleId?: string
  actions?: ReactNode
  description?: ReactNode
  className?: string
  labelledBy?: string
  label?: string
  'data-testid'?: string
}

interface SectionShellProps extends SectionProps {
  chromeClasses: string
}

function SectionShell({
  children,
  title,
  titleId,
  actions,
  description,
  className,
  labelledBy,
  label,
  'data-testid': testId,
  chromeClasses,
}: SectionShellProps) {
  const generatedId = useId()
  const headingId = titleId ?? generatedId

  const classes = ['flex flex-col gap-6', chromeClasses, className]
    .filter(Boolean)
    .join(' ')

  return (
    <section
      aria-label={label}
      aria-labelledby={labelledBy ?? (title ? headingId : undefined)}
      className={classes}
      data-testid={testId}
    >
      {(title || actions || description) && (
        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            {title && (
              <h2
                className="font-display text-h2 font-semibold text-ink"
                id={headingId}
              >
                {title}
              </h2>
            )}
            {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
          </div>
          {description && <p className="text-ink-soft">{description}</p>}
        </div>
      )}
      {children}
    </section>
  )
}

export function Section(props: SectionProps) {
  return <SectionShell {...props} chromeClasses="" />
}

export function SectionCard(props: SectionProps) {
  return (
    <SectionShell
      {...props}
      chromeClasses="rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
    />
  )
}
