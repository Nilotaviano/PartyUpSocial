import { useEffect } from 'react'

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
 */
export function useDirtyGuard(dirty: boolean, options: DirtyGuardOptions = {}) {
  const { onBlockedNavigation } = options

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
      onBlockedNavigation?.(href)
    }
    document.addEventListener('click', handleClick, true)

    // Push a sentinel entry so a back/forward gesture surfaces as a
    // `popstate` this hook can catch before it leaves the current screen.
    window.history.pushState(null, '', window.location.href)
    const handlePopState = () => {
      window.history.pushState(null, '', window.location.href)
      onBlockedNavigation?.('back')
    }
    window.addEventListener('popstate', handlePopState)

    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload)
      document.removeEventListener('click', handleClick, true)
      window.removeEventListener('popstate', handlePopState)
    }
  }, [dirty, onBlockedNavigation])
}
