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
 * The listener-and-sentinel effect below depends on `[dirty]` alone — round
 * 1's F1 finding: every dirty page here passes an inline arrow for
 * `onBlockedNavigation`, a new function identity on every render, and a
 * `[dirty, onBlockedNavigation]` dependency array re-ran this effect (and
 * re-pushed the history sentinel) on every one of those renders — 24 typed
 * characters, 24 `pushState` calls, Back unusable after editing a form. The
 * callback is read through a ref, updated every render but never itself a
 * dependency, so exactly one sentinel is pushed per clean→dirty transition.
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
      // Reconciles the sentinel this effect instance pushed, on every
      // dirty→clean transition and on unmount: a same-URL `replaceState`
      // collapses it without a real navigation. Never `history.back()`
      // here — that fires a genuine `popstate` mid-cleanup, which would
      // race a legitimate discard navigation already in flight.
      window.history.replaceState(null, '', window.location.href)
    }
  }, [dirty])
}
