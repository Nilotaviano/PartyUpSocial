import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { InstallBanner } from './InstallBanner'

const DESKTOP_UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36'
const IPHONE_UA =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'

function mockUserAgent(userAgent: string) {
  Object.defineProperty(navigator, 'userAgent', { configurable: true, value: userAgent })
}

// ThemeToggle.test.tsx's local-mock idiom: jsdom has no display-mode support, so the standalone
// probe is answered by a per-test matchMedia stub rather than a global helper.
function mockDisplayMode(standalone: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: vi.fn().mockReturnValue({
      matches: standalone,
      media: '(display-mode: standalone)',
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }),
  })
}

const prompt = vi.fn()

/** Fires the real Chromium event the module captures at import time. */
function fireBeforeInstallPrompt() {
  const event = new Event('beforeinstallprompt', { cancelable: true })
  Object.assign(event, {
    prompt,
    userChoice: Promise.resolve({ outcome: 'accepted' as const }),
  })
  act(() => {
    window.dispatchEvent(event)
  })
  return event
}

/** The module holds one captured event for the whole file; `appinstalled` releases it. */
function releaseCapturedPrompt() {
  act(() => {
    window.dispatchEvent(new Event('appinstalled'))
  })
}

beforeEach(() => {
  localStorage.clear()
  mockUserAgent(DESKTOP_UA)
  mockDisplayMode(false)
  prompt.mockReset()
  prompt.mockResolvedValue(undefined)
})

afterEach(() => {
  cleanup()
  releaseCapturedPrompt()
  localStorage.clear()
  vi.restoreAllMocks()
})

describe('InstallBanner — promotion eligibility (D-220)', () => {
  it('stays hidden until the browser offers an install prompt_D220', () => {
    render(<InstallBanner />)

    expect(screen.queryByTestId('install-banner')).not.toBeInTheDocument()
  })

  it('shows once a beforeinstallprompt has been captured_D220', () => {
    render(<InstallBanner />)
    fireBeforeInstallPrompt()

    expect(screen.getByTestId('install-banner')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Install' })).toBeInTheDocument()
  })

  it('preventDefault keeps Chrome from showing its own mini-infobar_D220', () => {
    render(<InstallBanner />)
    const event = fireBeforeInstallPrompt()

    expect(event.defaultPrevented).toBe(true)
  })

  it('stays hidden when already running standalone_D220', () => {
    mockDisplayMode(true)
    render(<InstallBanner />)
    fireBeforeInstallPrompt()

    expect(screen.queryByTestId('install-banner')).not.toBeInTheDocument()
  })

  it('stays hidden when this browser already dismissed it_D220', () => {
    localStorage.setItem('partyup:install-dismissed', '1')
    render(<InstallBanner />)
    fireBeforeInstallPrompt()

    expect(screen.queryByTestId('install-banner')).not.toBeInTheDocument()
  })

  it('dismissing persists to this browser and hides the banner_D220', () => {
    render(<InstallBanner />)
    fireBeforeInstallPrompt()

    fireEvent.click(screen.getByRole('button', { name: 'Not now' }))

    expect(localStorage.getItem('partyup:install-dismissed')).toBe('1')
    expect(screen.queryByTestId('install-banner')).not.toBeInTheDocument()
  })

  it('the action shows the browser prompt only on the user click, never on mount_D220', async () => {
    render(<InstallBanner />)
    fireBeforeInstallPrompt()
    expect(prompt).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Install' }))

    await waitFor(() => expect(prompt).toHaveBeenCalledTimes(1))
  })
})

describe('InstallBanner — iOS share-sheet arm (D-220)', () => {
  it('promotes on iOS Safari even though no beforeinstallprompt ever fires_D220', () => {
    mockUserAgent(IPHONE_UA)
    render(<InstallBanner />)

    expect(screen.getByTestId('install-banner')).toBeInTheDocument()
  })

  it('the action opens the Add to Home Screen instructions instead of a prompt_D220', async () => {
    mockUserAgent(IPHONE_UA)
    render(<InstallBanner />)

    fireEvent.click(screen.getByRole('button', { name: 'Install' }))

    expect(
      await screen.findByRole('heading', { name: 'Add PartyUp to your Home Screen' }),
    ).toBeInTheDocument()
    expect(screen.getByText(/Share button/)).toBeInTheDocument()
    expect(prompt).not.toHaveBeenCalled()
  })

  it('Esc closes the instructions dialog_D220', async () => {
    mockUserAgent(IPHONE_UA)
    render(<InstallBanner />)

    fireEvent.click(screen.getByRole('button', { name: 'Install' }))
    await screen.findByRole('dialog')

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('stays hidden on an iPhone that already runs from the Home Screen_D220', () => {
    mockUserAgent(IPHONE_UA)
    Object.defineProperty(navigator, 'standalone', { configurable: true, value: true })
    render(<InstallBanner />)

    expect(screen.queryByTestId('install-banner')).not.toBeInTheDocument()
    Object.defineProperty(navigator, 'standalone', { configurable: true, value: undefined })
  })
})
