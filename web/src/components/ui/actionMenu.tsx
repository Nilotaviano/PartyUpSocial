import * as DropdownMenuPrimitive from '@radix-ui/react-dropdown-menu'
import { useId } from 'react'
import type { ReactNode, RefObject } from 'react'
import { useTranslation } from 'react-i18next'

/**
 * Row/page action menu (STYLING_OVERHAUL_DESIGN.md §3.2): a Radix DropdownMenu
 * behind an icon button, so destructive work stops leading as a resting red
 * button. Destructive items are `text-danger` with plain words; the consequence
 * sentence is the item description, never a separate paragraph in the row.
 *
 * The trigger carries the icon-button recipe the D-139 class-duty loops grep
 * verbatim (`min-h-11 min-w-11` + the focus-visible triple), and items carry
 * `min-h-11` plus a `data-[highlighted]:` treatment — Radix menu items are
 * highlighted, not focus-visible, so the highlight is the state the loops must
 * see (`ActionMenu.test.tsx` is the duty fact).
 */

const itemBaseClasses =
  'group flex min-h-11 cursor-pointer select-none flex-col justify-center gap-0.5 rounded-[0.375rem] px-3 py-2 font-body text-body outline-none data-[disabled]:pointer-events-none data-[disabled]:opacity-50'

const itemToneClasses = {
  standard: 'text-ink data-[highlighted]:bg-accent data-[highlighted]:text-accent-ink',
  destructive:
    'text-danger data-[highlighted]:bg-danger data-[highlighted]:text-danger-ink',
} as const

const triggerClasses =
  'inline-flex min-h-11 min-w-11 flex-none items-center justify-center rounded-control font-display text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

export interface ActionMenuProps {
  children: ReactNode
  /** Trigger accessible name. Defaults to `common:actions.more`. */
  label?: string
  align?: 'start' | 'center' | 'end'
  open?: boolean
  onOpenChange?: (open: boolean) => void
  triggerRef?: RefObject<HTMLButtonElement | null>
  triggerClassName?: string
  className?: string
  'data-testid'?: string
}

export function ActionMenu({
  children,
  label,
  align = 'end',
  open,
  onOpenChange,
  triggerRef,
  triggerClassName,
  className,
  'data-testid': testId,
}: ActionMenuProps) {
  const { t } = useTranslation('common')
  const contentClasses = [
    'min-w-52 max-w-72 overflow-hidden rounded-control border-2 border-ink bg-card p-1 font-body text-ink shadow-offset-card',
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    // Non-modal: Radix modal mode marks the rest of the page aria-hidden while
    // leaving links focusable, failing axe aria-hidden-focus (WCAG 4.1.2).
    <DropdownMenuPrimitive.Root modal={false} open={open} onOpenChange={onOpenChange}>
      <DropdownMenuPrimitive.Trigger asChild>
        <button
          aria-label={label ?? t('actions.more')}
          className={[triggerClasses, triggerClassName].filter(Boolean).join(' ')}
          data-testid={testId}
          ref={triggerRef}
          type="button"
        >
          <span aria-hidden="true">⋯</span>
        </button>
      </DropdownMenuPrimitive.Trigger>
      <DropdownMenuPrimitive.Portal>
        <DropdownMenuPrimitive.Content align={align} className={contentClasses} sideOffset={8}>
          {children}
        </DropdownMenuPrimitive.Content>
      </DropdownMenuPrimitive.Portal>
    </DropdownMenuPrimitive.Root>
  )
}

export interface ActionMenuItemProps {
  children: ReactNode
  /** Consequence copy in meta type, inside the item (§3.2). */
  description?: ReactNode
  destructive?: boolean
  disabled?: boolean
  /** Mirrors an in-flight mutation on the item (aria-busy). */
  busy?: boolean
  onSelect?: (event: Event) => void
  asChild?: boolean
  className?: string
  'data-testid'?: string
}

export function ActionMenuItem({
  children,
  description,
  destructive = false,
  disabled,
  busy,
  onSelect,
  asChild,
  className,
  'data-testid': testId,
}: ActionMenuItemProps) {
  const labelId = useId()
  const descriptionId = useId()
  const classes = [
    itemBaseClasses,
    itemToneClasses[destructive ? 'destructive' : 'standard'],
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <DropdownMenuPrimitive.Item
      asChild={asChild}
      aria-busy={busy}
      aria-labelledby={!asChild && description ? labelId : undefined}
      aria-describedby={!asChild && description ? descriptionId : undefined}
      className={classes}
      data-testid={testId}
      disabled={disabled}
      onSelect={onSelect}
    >
      {asChild ? (
        children
      ) : (
        <>
          <span id={description ? labelId : undefined} className="truncate">
            {children}
          </span>
          {description && (
            <span
              id={descriptionId}
              className="font-body text-meta text-ink-soft group-data-[highlighted]:text-current"
            >
              {description}
            </span>
          )}
        </>
      )}
    </DropdownMenuPrimitive.Item>
  )
}
