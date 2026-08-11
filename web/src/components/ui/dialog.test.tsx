import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { Dialog } from './dialog'

/**
 * WeekGrid's sticky headers are positioned elements at z-20/z-10 in the root
 * stacking context, and Radix portals dialogs to document.body — so a dialog
 * that declares no layer sits at z-index:auto and loses to them.
 */
function openDialog(overlayClassName?: string, contentClassName?: string) {
  render(
    <Dialog.Root open>
      <Dialog.Portal>
        <Dialog.Overlay className={overlayClassName} data-testid="overlay" />
        <Dialog.Content className={contentClassName} data-testid="content">
          <Dialog.Title>Title</Dialog.Title>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>,
  )
}

afterEach(cleanup)

describe('Dialog layering', () => {
  it('puts the overlay and content above the calendar sticky headers by default', () => {
    openDialog('fixed inset-0 bg-scrim', 'fixed left-1/2 top-1/2')

    expect(screen.getByTestId('overlay').className).toContain('z-40')
    expect(screen.getByTestId('content').className).toContain('z-50')
  })

  it('leaves an explicit layer from the call site alone', () => {
    openDialog('fixed inset-0 z-30 bg-scrim', 'fixed z-[60] left-1/2')

    const overlay = screen.getByTestId('overlay').className
    const content = screen.getByTestId('content').className

    expect(overlay).toContain('z-30')
    expect(overlay).not.toContain('z-40')
    expect(content).toContain('z-[60]')
    expect(content).not.toContain('z-50')
  })
})
