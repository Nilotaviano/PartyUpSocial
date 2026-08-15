import type { ComponentType, ReactNode } from 'react'

/** Standard section container: icon + title + optional action, card surface. */
export function SectionCard({
  title,
  icon: Icon,
  action,
  children,
  className,
}: {
  title: string
  icon?: ComponentType<{ className?: string }>
  action?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={['surface p-4', className].filter(Boolean).join(' ')}>
      <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
        <h2 className="flex min-w-0 items-center gap-2 font-display text-base font-extrabold">
          {Icon && <Icon className="size-4 shrink-0" />}
          <span className="truncate">{title}</span>
        </h2>
        {action}
      </div>
      <div className="mt-3">{children}</div>
    </section>
  )
}

/** Consistent empty state: icon disc, headline, one line of guidance, one CTA. */
export function EmptyState({
  icon: Icon,
  title,
  description,
  action,
}: {
  icon: ComponentType<{ className?: string }>
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <div className="rounded-xl border border-dashed border-border bg-muted/40 px-4 py-8 text-center">
      <span className="mx-auto grid size-12 place-items-center rounded-full bg-card ring-1 ring-border">
        <Icon className="size-5 text-muted-foreground" />
      </span>
      {/* h3, not the prototype's <p>: the pre-shell primitive exposed empty
          states to heading navigation and the owner ruled that semantics stays. */}
      <h3 className="mt-3 font-display text-[15px] font-extrabold">{title}</h3>
      {description && (
        <p className="mx-auto mt-1 max-w-[36ch] text-[12.5px] text-muted-foreground">{description}</p>
      )}
      {action && <div className="mt-4 flex justify-center">{action}</div>}
    </div>
  )
}
