import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { ActionMenu, ActionMenuItem } from './actionMenu'

/**
 * D-139 class-duty fact at component level (STYLING_OVERHAUL_DESIGN.md §3.2/§8):
 * every ActionMenu item must clear the 44px target floor and carry a visible
 * highlight treatment. Radix menu items are highlighted (`data-highlighted`),
 * never `:focus-visible`, so the highlight is what the duty asserts here.
 */

function openMenu(trigger: HTMLElement) {
  // Radix's trigger opens on `pointerdown` (mouse), not `click`.
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
}

function renderMenu(onRemove = vi.fn()) {
  render(
    <ActionMenu>
      <ActionMenuItem>Invite to session</ActionMenuItem>
      <ActionMenuItem description="They stop seeing your sessions." destructive onSelect={onRemove}>
        Remove friend
      </ActionMenuItem>
    </ActionMenu>,
  )
  return onRemove
}

afterEach(cleanup)

describe('ActionMenu — class duty (D139)', () => {
  it('every menu item clears 44px and carries a highlight treatment_D139', async () => {
    renderMenu()

    openMenu(screen.getByRole('button', { name: 'More actions' }))
    const items = await screen.findAllByRole('menuitem')

    expect(items).toHaveLength(2)
    for (const item of items) {
      expect(item.className).toContain('min-h-11')
      expect(item.className).toMatch(/data-\[highlighted\]:/)
    }
  })

  it('the trigger carries the icon-button recipe and the focus-ring triple_D139', () => {
    renderMenu()

    const trigger = screen.getByRole('button', { name: 'More actions' })

    expect(trigger.className).toContain('min-h-11')
    expect(trigger.className).toContain('min-w-11')
    expect(trigger.className).toContain('focus-visible:outline-2')
    expect(trigger.className).toContain('focus-visible:outline-offset-2')
    expect(trigger.className).toContain('focus-visible:outline-focus')
  })

  it('a destructive item reads as danger text and states its consequence in the item_D139', async () => {
    renderMenu()

    openMenu(screen.getByRole('button', { name: 'More actions' }))
    const destructive = await screen.findByRole('menuitem', {
      name: /Remove friend/,
    })

    expect(destructive.className).toContain('text-danger')
    expect(destructive).toHaveTextContent('They stop seeing your sessions.')
  })
})

describe('ActionMenu — keyboard path', () => {
  it('opens with Enter, arrows through the items, and Escape returns focus to the trigger', async () => {
    renderMenu()

    const trigger = screen.getByRole('button', { name: 'More actions' })
    trigger.focus()
    fireEvent.keyDown(trigger, { key: 'Enter', code: 'Enter' })

    const items = await screen.findAllByRole('menuitem')
    await waitFor(() => expect(items[0]).toHaveFocus())

    fireEvent.keyDown(items[0], { key: 'ArrowDown', code: 'ArrowDown' })
    await waitFor(() => expect(items[1]).toHaveFocus())

    fireEvent.keyDown(items[1], { key: 'Escape', code: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('menuitem')).not.toBeInTheDocument())
    expect(trigger).toHaveFocus()
  })
})
