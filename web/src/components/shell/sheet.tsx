/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import type { ComponentProps, ReactNode } from 'react'
import { X } from 'lucide-react'
import { Dialog } from '../ui/dialog'

/**
 * Right-side sheet panel, built on the same Radix Dialog primitive
 * `ui/dialog.tsx` wraps (so it inherits that module's z-40/z-50 layering
 * over the calendar's sticky headers) — only the `right` side the shell
 * needs is implemented, unlike the prototype's shadcn `sheet.tsx` which
 * supports all four. Focus trap and Esc-to-close come from Radix Dialog
 * itself.
 */
export const Sheet = Dialog.Root
export const SheetTrigger = Dialog.Trigger
export const SheetClose = Dialog.Close
export const SheetTitle = Dialog.Title
export const SheetDescription = Dialog.Description

export function SheetOverlay(props: ComponentProps<typeof Dialog.Overlay>) {
  return (
    <Dialog.Overlay
      className="fixed inset-0 bg-scrim data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:animate-in data-[state=open]:fade-in-0"
      {...props}
    />
  )
}

export function SheetContent({
  className,
  children,
  closeLabel,
  ...props
}: ComponentProps<typeof Dialog.Content> & { closeLabel: string; children: ReactNode }) {
  return (
    <Dialog.Portal>
      <SheetOverlay />
      <Dialog.Content
        className={[
          'fixed inset-y-0 right-0 z-50 flex h-full w-[85vw] max-w-sm flex-col overflow-y-auto border-l border-border bg-card shadow-[var(--shadow-card)] outline-none data-[state=closed]:animate-out data-[state=closed]:slide-out-to-right data-[state=open]:animate-in data-[state=open]:slide-in-from-right',
          className,
        ]
          .filter(Boolean)
          .join(' ')}
        {...props}
      >
        {children}
        <Dialog.Close className="absolute right-3 top-3 inline-flex size-9 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus">
          <X aria-hidden className="size-4" />
          <span className="sr-only">{closeLabel}</span>
        </Dialog.Close>
      </Dialog.Content>
    </Dialog.Portal>
  )
}

export function SheetHeader({ className, ...props }: ComponentProps<'div'>) {
  return (
    <div
      className={['space-y-1 border-b border-border px-4 py-4 pr-12', className].filter(Boolean).join(' ')}
      {...props}
    />
  )
}
