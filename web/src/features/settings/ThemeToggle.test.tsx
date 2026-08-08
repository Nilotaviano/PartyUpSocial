import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { THEME_STORAGE_KEY } from '../../theme'
import { ThemeToggle } from './ThemeToggle'

type MediaListener = (event: { matches: boolean }) => void

function mockMatchMedia(initialMatches: boolean) {
  let matches = initialMatches
  const listeners = new Set<MediaListener>()
  const mediaQueryList = {
    get matches() {
      return matches
    },
    media: '(prefers-color-scheme: dark)',
    addEventListener: (_type: string, listener: MediaListener) =>
      listeners.add(listener),
    removeEventListener: (_type: string, listener: MediaListener) =>
      listeners.delete(listener),
  }
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: vi.fn().mockReturnValue(mediaQueryList),
  })

  return {
    triggerChange(next: boolean) {
      matches = next
      listeners.forEach((listener) => listener({ matches: next }))
    },
  }
}

function resetTheme() {
  localStorage.removeItem(THEME_STORAGE_KEY)
  document.documentElement.removeAttribute('data-theme')
}

beforeEach(resetTheme)

afterEach(() => {
  cleanup()
  resetTheme()
  vi.restoreAllMocks()
})

describe('ThemeToggle', () => {
  it('persists an explicit selection to localStorage and applies data-theme_D181', () => {
    mockMatchMedia(false)
    render(<ThemeToggle />)

    fireEvent.click(screen.getByRole('radio', { name: 'Dark' }))

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    expect(screen.getByRole('radio', { name: 'Dark' })).toHaveAttribute(
      'aria-checked',
      'true',
    )

    fireEvent.click(screen.getByRole('radio', { name: 'Light' }))

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  })

  it('clears localStorage and resolves the system theme when Match system is chosen_D181', () => {
    mockMatchMedia(true)
    render(<ThemeToggle />)

    fireEvent.click(screen.getByRole('radio', { name: 'Dark' }))
    fireEvent.click(screen.getByRole('radio', { name: 'Match system' }))

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull()
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
  })

  it('follows an OS prefers-color-scheme change while system is selected_D181', () => {
    const media = mockMatchMedia(false)
    render(<ThemeToggle />)

    expect(
      screen.getByRole('radio', { name: 'Match system' }),
    ).toHaveAttribute('aria-checked', 'true')

    media.triggerChange(true)
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')

    media.triggerChange(false)
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  })
})
