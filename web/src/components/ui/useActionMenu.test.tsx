import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import '../../i18n'
import { ActionMenu, ActionMenuItem } from './actionMenu'
import { Dialog } from './dialog'
import { useActionMenu, useActionMenuDialog } from './useActionMenu'

/**
 * The dialog-composition half of §3.2: a menu item that raises a dialog must
 * close its own menu, and closing the dialog must land focus back on the menu
 * trigger rather than on the body.
 */
function MenuWithDialog() {
  const menu = useActionMenu()
  const remove = useActionMenuDialog(menu)

  return (
    <>
      <ActionMenu {...menu.menuProps}>
        <ActionMenuItem destructive description="They stop seeing your sessions." {...remove.itemProps}>
          Remove friend
        </ActionMenuItem>
      </ActionMenu>
      <Dialog.Root {...remove.dialogProps}>
        <Dialog.Portal>
          <Dialog.Overlay />
          <Dialog.Content {...remove.contentProps} aria-describedby={undefined}>
            <Dialog.Title>Remove @friend.one?</Dialog.Title>
            <Dialog.Close asChild>
              <button type="button">Cancel</button>
            </Dialog.Close>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  )
}

function openMenu() {
  const trigger = screen.getByRole('button', { name: 'More actions' })
  // Radix's trigger opens on `pointerdown` (mouse), not `click`.
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
  return trigger
}

afterEach(cleanup)

describe('useActionMenuDialog', () => {
  it('a menu item opens the controlled dialog and closes the menu', async () => {
    render(<MenuWithDialog />)

    openMenu()
    fireEvent.click(await screen.findByRole('menuitem', { name: /Remove friend/ }))

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('menuitem')).not.toBeInTheDocument())
  })

  it('closing the dialog returns focus to the menu trigger', async () => {
    render(<MenuWithDialog />)

    const trigger = openMenu()
    fireEvent.click(await screen.findByRole('menuitem', { name: /Remove friend/ }))
    await screen.findByRole('dialog')

    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(trigger).toHaveFocus())
  })
})
