import { cleanup, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useDirtyGuard } from './useDirtyGuard'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

function dispatchBeforeUnload() {
  const event = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent
  window.dispatchEvent(event)
  return event
}

function clickInternalLink(href: string, init: MouseEventInit = {}) {
  const anchor = document.createElement('a')
  anchor.setAttribute('href', href)
  document.body.appendChild(anchor)
  const event = new MouseEvent('click', {
    bubbles: true,
    cancelable: true,
    button: 0,
    ...init,
  })
  anchor.dispatchEvent(event)
  document.body.removeChild(anchor)
  return event
}

describe('useDirtyGuard warns on navigating away or closing a dirty form_D152_D162', () => {
  it('prevents beforeunload while dirty (the closing half)', () => {
    renderHook(() => useDirtyGuard(true))

    const event = dispatchBeforeUnload()

    expect(event.defaultPrevented).toBe(true)
  })

  it('does not touch beforeunload when the form is clean', () => {
    renderHook(() => useDirtyGuard(false))

    const event = dispatchBeforeUnload()

    expect(event.defaultPrevented).toBe(false)
  })

  it('intercepts a click on an internal link while dirty (the router half)', () => {
    const onBlockedNavigation = vi.fn()
    renderHook(() => useDirtyGuard(true, { onBlockedNavigation }))

    const event = clickInternalLink('/sessions/upcoming')

    expect(event.defaultPrevented).toBe(true)
    expect(onBlockedNavigation).toHaveBeenCalledWith('/sessions/upcoming')
  })

  it('lets an internal-link click through untouched when the form is clean', () => {
    const onBlockedNavigation = vi.fn()
    renderHook(() => useDirtyGuard(false, { onBlockedNavigation }))

    const event = clickInternalLink('/sessions/upcoming')

    expect(event.defaultPrevented).toBe(false)
    expect(onBlockedNavigation).not.toHaveBeenCalled()
  })

  it('never intercepts a modified click (new-tab/download intent) even while dirty', () => {
    const onBlockedNavigation = vi.fn()
    renderHook(() => useDirtyGuard(true, { onBlockedNavigation }))

    const event = clickInternalLink('/sessions/upcoming', { ctrlKey: true })

    expect(event.defaultPrevented).toBe(false)
    expect(onBlockedNavigation).not.toHaveBeenCalled()
  })

  it('neutralizes a browser back/forward gesture while dirty and reports it as "back"', () => {
    const onBlockedNavigation = vi.fn()
    const pushStateSpy = vi.spyOn(window.history, 'pushState')
    renderHook(() => useDirtyGuard(true, { onBlockedNavigation }))
    pushStateSpy.mockClear()

    window.dispatchEvent(new PopStateEvent('popstate'))

    expect(onBlockedNavigation).toHaveBeenCalledWith('back')
    expect(pushStateSpy).toHaveBeenCalled()
  })

  it('stops intercepting once the hook unmounts', () => {
    const onBlockedNavigation = vi.fn()
    const { unmount } = renderHook(() => useDirtyGuard(true, { onBlockedNavigation }))

    unmount()
    const event = clickInternalLink('/sessions/upcoming')

    expect(event.defaultPrevented).toBe(false)
    expect(onBlockedNavigation).not.toHaveBeenCalled()
  })
})
