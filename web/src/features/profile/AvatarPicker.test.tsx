import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'

import '../../i18n'
import { AVATAR_CATALOG } from './avatarCatalog'
import { AvatarPicker } from './AvatarPicker'

function Harness({ initial }: { initial: string }) {
  const [value, setValue] = useState(initial)
  return <AvatarPicker idPrefix="test-avatar" value={value} onChange={setValue} />
}

afterEach(cleanup)

describe('AvatarPicker', () => {
  it('moves selection and focus with ArrowRight and ArrowDown_APG', () => {
    render(<Harness initial="🎮" />)

    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveAttribute('aria-checked', 'true')
    expect(first).toHaveAttribute('tabindex', '0')

    fireEvent.keyDown(first, { key: 'ArrowRight' })
    const second = screen.getByRole('radio', { name: 'Dice' })
    expect(second).toHaveAttribute('aria-checked', 'true')
    expect(second).toHaveFocus()
    expect(first).toHaveAttribute('tabindex', '-1')

    fireEvent.keyDown(second, { key: 'ArrowDown' })
    const third = screen.getByRole('radio', { name: 'Joystick' })
    expect(third).toHaveAttribute('aria-checked', 'true')
    expect(third).toHaveFocus()
  })

  it('moves selection backwards with ArrowLeft and ArrowUp_APG', () => {
    render(<Harness initial="🕹️" />)

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Joystick' }), { key: 'ArrowLeft' })
    expect(screen.getByRole('radio', { name: 'Dice' })).toHaveFocus()

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Dice' }), { key: 'ArrowUp' })
    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveFocus()
    expect(first).toHaveAttribute('aria-checked', 'true')
  })

  it('wraps at both ends of the catalog_APG', () => {
    render(<Harness initial="🎮" />)

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Game controller' }), { key: 'ArrowLeft' })
    const last = screen.getByRole('radio', { name: 'Coffee' })
    expect(last).toHaveAttribute('aria-checked', 'true')
    expect(last).toHaveFocus()

    fireEvent.keyDown(last, { key: 'ArrowRight' })
    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveAttribute('aria-checked', 'true')
    expect(first).toHaveFocus()
  })

  it('ignores keys that are not arrows_APG', () => {
    render(<Harness initial="🎮" />)
    const first = screen.getByRole('radio', { name: 'Game controller' })
    fireEvent.keyDown(first, { key: 'a' })
    expect(first).toHaveAttribute('aria-checked', 'true')
  })

  it('names every radio from its catalog label and keeps the glyph decorative_D188', () => {
    render(<Harness initial="🎮" />)

    expect(screen.getByRole('radiogroup', { name: 'Choose an avatar' })).toBeInTheDocument()
    const radios = screen.getAllByRole('radio')
    expect(radios).toHaveLength(AVATAR_CATALOG.length)
    radios.forEach((radio, index) => {
      const entry = AVATAR_CATALOG[index]
      expect(radio.getAttribute('aria-label')).toBeTruthy()
      expect(radio.getAttribute('aria-label')).not.toContain(entry.glyph)
      expect(radio.querySelector('span[aria-hidden="true"]')).toHaveTextContent(entry.glyph)
    })
    expect(screen.getByRole('radio', { name: 'Dragon' })).toHaveTextContent('🐉')
    expect(screen.queryByRole('radio', { name: '🐉' })).toBeNull()
  })

  it('lets the caller name the group from visible page text instead_APG', () => {
    render(
      <>
        <p id="page-avatar-label">Avatar</p>
        <AvatarPicker
          idPrefix="test-avatar"
          labelledBy="page-avatar-label"
          value="🎮"
          onChange={() => {}}
        />
      </>,
    )

    expect(screen.getByRole('radiogroup', { name: 'Avatar' })).not.toHaveAttribute('aria-label')
    expect(screen.queryByRole('radiogroup', { name: 'Choose an avatar' })).toBeNull()
  })

  it('reports the chosen glyph through onChange on click', () => {
    render(<Harness initial="🎮" />)
    fireEvent.click(screen.getByRole('radio', { name: 'Fox' }))
    expect(screen.getByRole('radio', { name: 'Fox' })).toHaveAttribute('aria-checked', 'true')
  })

  it('extends the same radiogroup with the caller sources, appended after the catalog_D190', () => {
    function SourceHarness() {
      const [value, setValue] = useState('🎮')
      return (
        <AvatarPicker
          idPrefix="test-avatar"
          sources={[
            { avatarSourceId: 'src-1', kind: 'SteamPlatform', imgUrl: '/a/steam-token' },
            { avatarSourceId: 'src-2', kind: 'GoogleAccount', imgUrl: '/a/google-token' },
          ]}
          value={value}
          onChange={setValue}
        />
      )
    }
    render(<SourceHarness />)

    expect(screen.getAllByRole('radio')).toHaveLength(AVATAR_CATALOG.length + 2)
    const steamRadio = screen.getByRole('radio', { name: 'Steam picture' })
    expect(steamRadio.querySelector('img')).toHaveAttribute('src', '/a/steam-token')
    const googleRadio = screen.getByRole('radio', { name: 'Google picture' })
    expect(googleRadio.querySelector('img')).toHaveAttribute('src', '/a/google-token')

    fireEvent.click(steamRadio)
    expect(steamRadio).toHaveAttribute('aria-checked', 'true')

    // ArrowLeft from the first source option steps back into the last catalog glyph —
    // one radiogroup, catalog and sources navigated as a single sequence.
    fireEvent.keyDown(steamRadio, { key: 'ArrowLeft' })
    const lastCatalogEntry = AVATAR_CATALOG[AVATAR_CATALOG.length - 1]
    const lastCatalogRadio = screen.getAllByRole('radio')[AVATAR_CATALOG.length - 1]
    expect(lastCatalogRadio).toHaveFocus()
    expect(lastCatalogRadio).toHaveAttribute('aria-checked', 'true')
    expect(lastCatalogRadio).toHaveTextContent(lastCatalogEntry.glyph)

    // ArrowRight from there wraps forward past the whole catalog into the sources.
    fireEvent.keyDown(lastCatalogRadio, { key: 'ArrowRight' })
    expect(steamRadio).toHaveFocus()
    expect(steamRadio).toHaveAttribute('aria-checked', 'true')
  })

  it('falls back to the glyph placeholder when a source thumbnail fails to load', () => {
    function SourceHarness() {
      const [value, setValue] = useState('🎮')
      return (
        <AvatarPicker
          idPrefix="test-avatar"
          sources={[{ avatarSourceId: 'src-1', kind: 'SteamPlatform', imgUrl: '/a/broken-token' }]}
          value={value}
          onChange={setValue}
        />
      )
    }
    render(<SourceHarness />)

    const steamRadio = screen.getByRole('radio', { name: 'Steam picture' })
    const img = steamRadio.querySelector('img')
    expect(img).not.toBeNull()
    fireEvent.error(img!)

    expect(steamRadio.querySelector('img')).toBeNull()
    expect(steamRadio).toHaveTextContent('●')
  })
})
