import { useCallback, useRef, useState, type RefObject } from 'react'

import type { ActionMenuProps } from './actionMenu'

export interface ActionMenuController {
  open: boolean
  setOpen: (open: boolean) => void
  triggerRef: RefObject<HTMLButtonElement | null>
  menuProps: Pick<ActionMenuProps, 'open' | 'onOpenChange' | 'triggerRef'>
}

/**
 * Controls one ActionMenu so items that open a dialog can close the menu
 * themselves and hand focus back to the trigger afterwards.
 */
export function useActionMenu(): ActionMenuController {
  const triggerRef = useRef<HTMLButtonElement | null>(null)
  const [open, setOpen] = useState(false)

  return {
    open,
    setOpen,
    triggerRef,
    menuProps: { open, onOpenChange: setOpen, triggerRef },
  }
}

export interface ActionMenuDialog {
  open: boolean
  /** Spread onto the `ActionMenuItem` that opens this dialog. */
  itemProps: { onSelect: (event: Event) => void }
  /** Spread onto the controlled `Dialog.Root` rendered beside the menu. */
  dialogProps: { open: boolean; onOpenChange: (open: boolean) => void }
  /** Spread onto that dialog's `Dialog.Content`. */
  contentProps: { onCloseAutoFocus: (event: Event) => void }
}

/**
 * Dialog composition contract (§3.2). The item's `onSelect` calls
 * `preventDefault` so Radix does not run its own close-and-restore-focus pass
 * while the dialog is mounting (the two focus moves race, and the loser is the
 * dialog); the menu is closed explicitly instead.
 *
 * Closing the dialog puts focus back on the menu **trigger**, and it does so
 * from `onCloseAutoFocus` rather than from the open-state change: the element
 * that held focus when the dialog opened was a menu item that no longer exists,
 * so Radix's own restore would leave focus on the body.
 *
 * Usage — the dialog is a sibling of the menu, never a child of its content,
 * which unmounts on close. Call once per dialog item:
 *
 * ```tsx
 * const menu = useActionMenu()
 * const remove = useActionMenuDialog(menu)
 * <ActionMenu {...menu.menuProps}>
 *   <ActionMenuItem destructive description={t('friends:remove.consequence')} {...remove.itemProps}>
 *     {t('friends:remove.action')}
 *   </ActionMenuItem>
 * </ActionMenu>
 * <Dialog.Root {...remove.dialogProps}>
 *   …<Dialog.Content {...remove.contentProps}>…</Dialog.Content>
 * </Dialog.Root>
 * ```
 */
export function useActionMenuDialog(menu: ActionMenuController): ActionMenuDialog {
  const [open, setOpen] = useState(false)
  const { setOpen: setMenuOpen, triggerRef } = menu

  const onSelect = useCallback(
    (event: Event) => {
      event.preventDefault()
      setMenuOpen(false)
      setOpen(true)
    },
    [setMenuOpen],
  )

  const onCloseAutoFocus = useCallback(
    (event: Event) => {
      event.preventDefault()
      triggerRef.current?.focus()
    },
    [triggerRef],
  )

  return {
    open,
    itemProps: { onSelect },
    dialogProps: { open, onOpenChange: setOpen },
    contentProps: { onCloseAutoFocus },
  }
}
