import { useCallback, useState, type ReactNode } from 'react'
import { PageSlotContext, usePageSlotRegistry } from './pageSlotContext'

/**
 * The page column (STYLING_OVERHAUL_DESIGN.md §3.1). Shells own the chrome and
 * pin the footer; pages keep their own `<main>` and wrap its content in this.
 * `width="form"` is the narrow column single-purpose forms sit in.
 */
export interface PageScaffoldProps {
  children: ReactNode
  className?: string
  width?: 'page' | 'form'
}

const widthClasses = {
  page: 'max-w-page',
  form: 'max-w-form',
} as const

export function PageScaffold({
  children,
  className,
  width = 'page',
}: PageScaffoldProps) {
  const classes = [
    'mx-auto flex w-full flex-col gap-8 px-4 py-6 sm:px-8',
    widthClasses[width],
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return <div className={classes}>{children}</div>
}

/**
 * The shell half of the portal slot (STYLING_OVERHAUL_DESIGN.md §3.5/R3): it
 * holds whichever slot the mounted page currently offers.
 */
export function PageSlotProvider({ children }: { children: ReactNode }) {
  const [target, setTarget] = useState<HTMLElement | null>(null)
  // React detaches the leaving page's ref before attaching the arriving one,
  // so a plain assignment never leaves a stale (unmounted) target behind.
  const register = useCallback((node: HTMLElement | null) => setTarget(node), [])

  return (
    <PageSlotContext.Provider value={{ target, register }}>
      {children}
    </PageSlotContext.Provider>
  )
}

/**
 * The page half of the slot: pages place it where the shell content belongs
 * (below their PageHeader). Outside a provider it renders an inert container,
 * so a page is never coupled to the shell that hosts it.
 */
export function PageSlot({ className }: { className?: string }) {
  const registry = usePageSlotRegistry()
  const classes = ['empty:hidden', className].filter(Boolean).join(' ')

  return <div className={classes} ref={registry?.register} />
}
