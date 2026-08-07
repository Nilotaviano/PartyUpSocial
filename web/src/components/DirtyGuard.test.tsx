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

    // The mount itself pushes the one clean→dirty sentinel — counted here,
    // not cleared away, so the assertion below is honest about the total.
    expect(pushStateSpy).toHaveBeenCalledTimes(1)

    window.dispatchEvent(new PopStateEvent('popstate'))

    expect(onBlockedNavigation).toHaveBeenCalledWith('back')
    expect(pushStateSpy).toHaveBeenCalledTimes(2)
  })

  it('stops intercepting once the hook unmounts', () => {
    const onBlockedNavigation = vi.fn()
    const { unmount } = renderHook(() => useDirtyGuard(true, { onBlockedNavigation }))

    unmount()
    const event = clickInternalLink('/sessions/upcoming')

    expect(event.defaultPrevented).toBe(false)
    expect(onBlockedNavigation).not.toHaveBeenCalled()
  })

  // `onBlockedNavigation` is a fresh inline arrow on every render on both
  // real call sites (SessionCreatePage.tsx, SessionEditPage.tsx) — this
  // mirrors that exactly, a new callback identity every rerender while
  // dirty stays true throughout, rather than a stable `vi.fn()` across
  // rerenders, which cannot discriminate a dependency-array effect that
  // re-fires per render (a stable reference never changes, so such an
  // effect never re-fires either way, masking the bug this proves absent).
  it('pushes exactly one history sentinel across many rerenders with a fresh callback identity each time_D152_D162', () => {
    const pushStateSpy = vi.spyOn(window.history, 'pushState')
    const calls: string[] = []
    const { rerender } = renderHook(
      ({ dirty }: { dirty: boolean }) =>
        useDirtyGuard(dirty, {
          // A genuinely new function on every render — the exact shape
          // `SessionCreatePage.tsx`/`SessionEditPage.tsx` pass from their
          // own render bodies, never memoized.
          onBlockedNavigation: (target: string) => {
            calls.push(target)
          },
        }),
      { initialProps: { dirty: true } },
    )

    for (let i = 0; i < 24; i++) {
      rerender({ dirty: true })
    }

    expect(pushStateSpy).toHaveBeenCalledTimes(1)
  })

  // The sentinel is a deliberate, accepted residual (see the hook's doc
  // comment): cleanup never touches history state, on either a
  // dirty→clean transition or an unmount while still dirty — rewriting or
  // popping the current entry there could race an in-flight navigation or
  // null out `location.state` (React Router's own `{usr,key,idx}`).
  it('leaves history state untouched across a dirty-to-clean transition and unmount', () => {
    const pushStateSpy = vi.spyOn(window.history, 'pushState')
    const replaceStateSpy = vi.spyOn(window.history, 'replaceState')
    const { rerender, unmount } = renderHook(
      ({ dirty }: { dirty: boolean }) => useDirtyGuard(dirty),
      { initialProps: { dirty: true } },
    )
    expect(pushStateSpy).toHaveBeenCalledTimes(1)
    const stateAfterMount = window.history.state

    rerender({ dirty: false })

    expect(pushStateSpy).toHaveBeenCalledTimes(1)
    expect(replaceStateSpy).not.toHaveBeenCalled()
    expect(window.history.state).toBe(stateAfterMount)

    // Clean now: a beforeunload/click is no longer intercepted, confirming
    // the listeners this effect installed were actually torn down.
    const event = dispatchBeforeUnload()
    expect(event.defaultPrevented).toBe(false)

    unmount()

    expect(pushStateSpy).toHaveBeenCalledTimes(1)
    expect(replaceStateSpy).not.toHaveBeenCalled()
    expect(window.history.state).toBe(stateAfterMount)
  })
})
