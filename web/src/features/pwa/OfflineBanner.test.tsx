import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { OfflineBanner } from './OfflineBanner'

function setOnline(online: boolean) {
  Object.defineProperty(navigator, 'onLine', { configurable: true, value: online })
}

function fireConnectivity(event: 'online' | 'offline') {
  act(() => {
    window.dispatchEvent(new Event(event))
  })
}

const OFFLINE_TEXT =
  'You are offline. What you can see may be out of date until the connection returns.'

beforeEach(() => {
  setOnline(true)
})

afterEach(() => {
  cleanup()
  setOnline(true)
  vi.restoreAllMocks()
})

describe('OfflineBanner (D-219)', () => {
  it('announces nothing while the connection is up_D219', () => {
    render(<OfflineBanner />)

    expect(screen.getByRole('status')).toHaveTextContent('')
  })

  it('announces the offline state when the browser goes offline_D219', () => {
    render(<OfflineBanner />)

    setOnline(false)
    fireConnectivity('offline')

    expect(screen.getByRole('status')).toHaveTextContent(OFFLINE_TEXT)
  })

  it('clears the announcement when the connection returns_D219', () => {
    render(<OfflineBanner />)
    setOnline(false)
    fireConnectivity('offline')
    expect(screen.getByRole('status')).toHaveTextContent(OFFLINE_TEXT)

    setOnline(true)
    fireConnectivity('online')

    expect(screen.getByRole('status')).toHaveTextContent('')
  })

  it('renders offline immediately when the page loads with no connection_D219', () => {
    setOnline(false)
    render(<OfflineBanner />)

    expect(screen.getByRole('status')).toHaveTextContent(OFFLINE_TEXT)
  })

  it('keeps one live region mounted across both edges, so the change is announced_D219', () => {
    render(<OfflineBanner />)
    const region = screen.getByRole('status')

    setOnline(false)
    fireConnectivity('offline')

    expect(screen.getByRole('status')).toBe(region)
  })
})
