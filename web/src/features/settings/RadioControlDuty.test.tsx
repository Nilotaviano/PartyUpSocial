import { cleanup, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { AvatarPicker } from '../profile/AvatarPicker'
import { THEME_STORAGE_KEY } from '../../theme'
import { ThemeToggle } from './ThemeToggle'

/**
 * D-139 class-duty loop for the two hand-rolled radio patterns
 * (STYLING_OVERHAUL_DESIGN.md §3.2, S5). Both groups are `role=radio` **button
 * elements**, so the 44px floor and the focus-ring triple are theirs to carry —
 * unlike the bare native finalize inputs, which satisfy the same duty through
 * their ≥44px wrapping-label row. The strings below are matched literally
 * because they are what the rest of the D-139 loops grep for.
 */
const DUTY_CLASSES = [
  'min-h-11',
  'min-w-11',
  'focus-visible:outline-2',
  'focus-visible:outline-offset-2',
  'focus-visible:outline-focus',
]

function AvatarHarness() {
  const [value, setValue] = useState('🎮')
  return <AvatarPicker idPrefix="duty-avatar" value={value} onChange={setValue} />
}

afterEach(() => {
  cleanup()
  localStorage.removeItem(THEME_STORAGE_KEY)
  document.documentElement.removeAttribute('data-theme')
  vi.restoreAllMocks()
})

describe('hand-rolled radio controls carry the touch-target and focus duty_D139', () => {
  it.each([
    ['ThemeToggle', () => <ThemeToggle />],
    ['AvatarPicker', () => <AvatarHarness />],
  ])('%s', (_name, element) => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn().mockReturnValue({
        matches: false,
        media: '(prefers-color-scheme: dark)',
        addEventListener: () => {},
        removeEventListener: () => {},
      }),
    })
    render(element())

    const radios = screen
      .getAllByRole('radio')
      .filter((radio) => radio.tagName === 'BUTTON')
    expect(radios.length).toBeGreaterThan(0)
    for (const radio of radios) {
      for (const duty of DUTY_CLASSES) {
        expect(radio.className).toContain(duty)
      }
    }
  })
})
