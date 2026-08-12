import { cleanup, fireEvent, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { AvatarDisc, type AvatarDiscProps } from './AvatarDisc'

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

  it('renders the proxy image inside the decorative span when imgUrl is set', () => {
    const { container } = render(
      <AvatarDisc emoji="🎲" imgUrl="/a/proxy-token" seed="user-1" />,
    )

    const disc = container.querySelector('span[aria-hidden="true"]')
    expect(disc).not.toBeNull()
    const img = disc?.querySelector('img')
    expect(img).not.toBeNull()
    expect(img).toHaveAttribute('src', '/a/proxy-token')
    expect(img).toHaveAttribute('alt', '')
    expect(disc).not.toHaveTextContent('🎲')
  })

  it('falls back to the glyph, never the masked treatment, when the image fails to load', () => {
    const { container } = render(
      <AvatarDisc emoji="🎲" imgUrl="/a/broken-token" seed="user-1" />,
    )

    const img = container.querySelector('img')
    expect(img).not.toBeNull()
    fireEvent.error(img!)

    const disc = container.querySelector('span[aria-hidden="true"]')
    expect(disc?.querySelector('img')).toBeNull()
    expect(disc).toHaveTextContent('🎲')
    // A transient failure must not read as a masked/anonymized identity.
    expect(disc).not.toHaveClass('bg-busy')
  })

  it('never renders a picture for a masked identity even when imgUrl is set_D076', () => {
    const { container } = render(
      <AvatarDisc emoji="🎲" imgUrl="/a/proxy-token" masked seed="anon-1" />,
    )

    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('span[aria-hidden="true"]')).toHaveTextContent('●')
  })

  it('retries a new imgUrl after a previous one failed, instead of staying on the glyph', () => {
    const { container, rerender } = render(
      <AvatarDisc emoji="🎲" imgUrl="/a/broken-token" seed="user-1" />,
    )

    fireEvent.error(container.querySelector('img')!)
    expect(container.querySelector('img')).toBeNull()

    const rerenderWith = (props: AvatarDiscProps) => rerender(<AvatarDisc {...props} />)
    rerenderWith({ emoji: '🎲', imgUrl: '/a/fresh-token', seed: 'user-1' })

    const img = container.querySelector('img')
    expect(img).not.toBeNull()
    expect(img).toHaveAttribute('src', '/a/fresh-token')
  })
})
