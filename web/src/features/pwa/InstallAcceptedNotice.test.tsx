import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { clearInstallAccepted, showNativeInstallPrompt } from './installPrompt'
import { InstallAcceptedNotice } from './InstallAcceptedNotice'

const ACCEPTED_TEXT = 'Installing — the PartyUp icon will appear on your home screen shortly.'

/** Fires the real Chromium event `installPrompt.ts` captures at module scope. */
function fireBeforeInstallPrompt(outcome: 'accepted' | 'dismissed') {
  const event = new Event('beforeinstallprompt', { cancelable: true })
  Object.assign(event, {
    prompt: vi.fn().mockResolvedValue(undefined),
    userChoice: Promise.resolve({ outcome }),
  })
  act(() => {
    window.dispatchEvent(event)
  })
}

afterEach(() => {
  cleanup()
  clearInstallAccepted()
  vi.useRealTimers()
})

describe('InstallAcceptedNotice (D-220)', () => {
  it('announces nothing before any install outcome_D220', () => {
    render(<InstallAcceptedNotice />)

    expect(screen.getByRole('status')).toHaveTextContent('')
  })

  it('announces the confirmation once the native prompt resolves accepted_D220', async () => {
    render(<InstallAcceptedNotice />)
    fireBeforeInstallPrompt('accepted')

    await act(async () => {
      await showNativeInstallPrompt()
    })

    expect(screen.getByRole('status')).toHaveTextContent(ACCEPTED_TEXT)
  })

  it('stays silent when the native prompt is dismissed_D220', async () => {
    render(<InstallAcceptedNotice />)
    fireBeforeInstallPrompt('dismissed')

    await act(async () => {
      await showNativeInstallPrompt()
    })

    expect(screen.getByRole('status')).toHaveTextContent('')
  })

  it('clears the confirmation on its own after the auto-clear window_D220', async () => {
    vi.useFakeTimers()
    render(<InstallAcceptedNotice />)
    fireBeforeInstallPrompt('accepted')

    await act(async () => {
      await showNativeInstallPrompt()
    })
    expect(screen.getByRole('status')).toHaveTextContent(ACCEPTED_TEXT)

    await act(async () => {
      vi.advanceTimersByTime(8000)
    })

    expect(screen.getByRole('status')).toHaveTextContent('')
  })

  it('keeps one live region mounted across the change, so it is announced_D220', async () => {
    render(<InstallAcceptedNotice />)
    const region = screen.getByRole('status')
    fireBeforeInstallPrompt('accepted')

    await act(async () => {
      await showNativeInstallPrompt()
    })

    expect(screen.getByRole('status')).toBe(region)
  })
})
