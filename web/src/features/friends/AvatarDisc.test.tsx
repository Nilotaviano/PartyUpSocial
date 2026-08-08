import { cleanup, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { AvatarDisc } from './AvatarDisc'

afterEach(() => {
  cleanup()
})

describe('AvatarDisc', () => {
  it('renders the busy pair and the neutral placeholder when masked_D076', () => {
    const { container } = render(
      <AvatarDisc emoji="🎲" masked seed="anon-1" />,
    )

    const disc = container.querySelector('span')
    expect(disc).not.toBeNull()
    expect(disc).toHaveClass('bg-busy')
    expect(disc).toHaveClass('text-busy-ink')
    expect(disc).toHaveClass('border-ink')
    // The real emoji never leaks through a masked identity.
    expect(disc).toHaveTextContent('●')
    expect(disc).not.toHaveTextContent('🎲')
  })
})
