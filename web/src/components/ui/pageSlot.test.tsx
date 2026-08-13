import { cleanup, render, screen } from '@testing-library/react'
import { createPortal } from 'react-dom'
import { afterEach, describe, expect, it } from 'vitest'

import { PageScaffold, PageSlot, PageSlotProvider } from './pageScaffold'
import { usePageSlot } from './pageSlotContext'

/**
 * The push prompt's mounting contract (STYLING_OVERHAUL_DESIGN.md §3.5/R3),
 * exercised on the primitive rather than on the card: a shell component
 * portals into the page's slot when the page offers one, and renders where the
 * shell put it when it does not — which is also what every provider-less
 * component test sees.
 */
function ShellMounted() {
  const slot = usePageSlot()
  const content = <p data-testid="shell-content">shell content</p>
  return slot ? createPortal(content, slot) : content
}

afterEach(cleanup)

describe('PageSlot', () => {
  it('portals shell content into the slot the mounted page offers', () => {
    render(
      <PageSlotProvider>
        <div data-testid="shell-position">
          <ShellMounted />
        </div>
        <main>
          <PageScaffold>
            <h1>Page</h1>
            <PageSlot />
          </PageScaffold>
        </main>
      </PageSlotProvider>,
    )

    const content = screen.getByTestId('shell-content')
    expect(content).toBeInTheDocument()
    expect(screen.getByTestId('shell-position')).not.toContainElement(content)
    expect(screen.getByRole('main')).toContainElement(content)
  })

  it('keeps the shell position when the page offers no slot', () => {
    render(
      <PageSlotProvider>
        <div data-testid="shell-position">
          <ShellMounted />
        </div>
        <main>
          <PageScaffold>
            <h1>Page</h1>
          </PageScaffold>
        </main>
      </PageSlotProvider>,
    )

    expect(screen.getByTestId('shell-position')).toContainElement(
      screen.getByTestId('shell-content'),
    )
  })

  it('renders in place with no provider above it at all', () => {
    render(<ShellMounted />)

    expect(screen.getByTestId('shell-content')).toBeInTheDocument()
  })
})
