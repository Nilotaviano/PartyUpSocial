import { useEffect, useRef } from 'react'

export type DirtyGuardOptions = {
  /**
   * Called instead of letting an in-app navigation through while the form
   * is dirty. `target` is the intercepted link's `href`, or `'back'` for a
   * browser back/forward gesture. The caller decides what "leave anyway"
   * means (usually: show the discard dialog, then perform the navigation
   * itself on confirm).
   */
  onBlockedNavigation?: (target: string) => void
}

/**
 * Warns before an unsaved create/edit form is discarded, both by
 * (a) closing the tab/reloading — `beforeunload` — and (b) navigating away
 * inside the app — intercepted here rather than through `useBlocker`, which
 * requires a data router; this SPA renders `<Routes>` under a plain
 * `<BrowserRouter>` (design §10.1, `TRACEABILITY.md:359`).
 *
 * In-app navigation is intercepted at two points: a left-click on an
 * internal `<a href="/...">` (every `react-router` `Link`/`NavLink`
 * ultimately renders one), and the browser back/forward gesture, which is
 * neutralized by immediately re-pushing the current entry so history state
 * cannot drift out from under a dirty form during the confirmation.
 *
 * The listener-and-sentinel effect below depends on `[dirty]` alone: every
 * dirty page here passes an inline arrow for `onBlockedNavigation`, a new
 * function identity on every render, so the callback is read through a ref
 * (updated every render but never itself a dependency) — exactly one
 * sentinel is pushed per clean→dirty transition, not once per keystroke.
 *
 * That sentinel is deliberately never popped back off on cleanup. A form
 * session that goes dirty therefore leaves one surplus same-URL history
 * entry behind — Back still works, it just costs one extra press on that
 * page, and the popstate interceptor below is inert once the form is clean
 * (this effect has already torn down). Removing it is unsafe, not merely
 * unimplemented: by the time cleanup runs, `navigate()` may already have
 * pushed the real destination on top of the sentinel, so popping or
 * rewriting the current entry then would race an in-flight navigation or
 * silently null out `location.state` (React Router's own `{usr,key,idx}`
 * — a `replaceState(null, ...)` here once did exactly that, breaking every
 * `location.state` consumer, e.g. `AuthScreens`' `returnTo`) and desync the
 * router's own history index. The one-extra-press residual is accepted in
 * trade for never touching history state this hook does not own.
 */
export function useDirtyGuard(dirty: boolean, options: DirtyGuardOptions = {}) {
  const onBlockedNavigationRef = useRef(options.onBlockedNavigation)
  useEffect(() => {
    onBlockedNavigationRef.current = options.onBlockedNavigation
  })

  useEffect(() => {
    if (!dirty) return

    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
    }
    window.addEventListener('beforeunload', handleBeforeUnload)

    const handleClick = (event: MouseEvent) => {
      if (
        event.defaultPrevented ||
        event.button !== 0 ||
        event.metaKey ||
        event.ctrlKey ||
        event.shiftKey ||
        event.altKey
      ) {
        return
      }

      const target = event.target
      if (!(target instanceof Element)) return
      const anchor = target.closest('a[href]')
      if (!anchor) return

      const href = anchor.getAttribute('href')
      if (!href || !href.startsWith('/') || href.startsWith('//')) return

      event.preventDefault()
      onBlockedNavigationRef.current?.(href)
    }
    document.addEventListener('click', handleClick, true)

    // Push a sentinel entry so a back/forward gesture surfaces as a
    // `popstate` this hook can catch before it leaves the current screen —
    // exactly once per clean→dirty transition (see the effect's own `[dirty]`
    // dependency array above).
    window.history.pushState(null, '', window.location.href)
    const handlePopState = () => {
      window.history.pushState(null, '', window.location.href)
      onBlockedNavigationRef.current?.('back')
    }
    window.addEventListener('popstate', handlePopState)

    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload)
      document.removeEventListener('click', handleClick, true)
      window.removeEventListener('popstate', handlePopState)
      // No history reconciliation here — see this hook's doc comment for
      // why popping or rewriting the sentinel on cleanup is unsafe.
    }
  }, [dirty])
}
