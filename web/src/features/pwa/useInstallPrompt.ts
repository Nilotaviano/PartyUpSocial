import { useCallback, useState, useSyncExternalStore } from 'react'
import {
  getDeferredInstallPrompt,
  hasDismissedInstallInThisBrowser,
  isIosSafari,
  isStandaloneDisplay,
  markInstallDismissedInThisBrowser,
  showNativeInstallPrompt,
  subscribeToInstallPrompt,
  type InstallOutcome,
} from './installPrompt'

export interface InstallPromptState {
  /** A captured `beforeinstallprompt` is in hand, so the browser's own dialog can be shown. */
  canPromptNative: boolean
  /** iOS Safari: no native prompt exists, so the share-sheet instructions are the action. */
  isIos: boolean
  /** Already running as an installed app — every install surface stays hidden. */
  standalone: boolean
  /** This browser dismissed the first-visit banner. */
  dismissed: boolean
  dismiss: () => void
  promptInstall: () => Promise<InstallOutcome>
}

/**
 * D-220's install-promotion state, shared by the first-visit banner and the AccountMenu row so
 * both offer the same action for the same browser. `standalone` and `isIos` are read during
 * render rather than latched in an effect: neither changes within a page's lifetime.
 */
export function useInstallPrompt(): InstallPromptState {
  const deferred = useSyncExternalStore(
    subscribeToInstallPrompt,
    getDeferredInstallPrompt,
    () => null,
  )
  const [dismissed, setDismissed] = useState(() => hasDismissedInstallInThisBrowser())

  const dismiss = useCallback(() => {
    markInstallDismissedInThisBrowser()
    setDismissed(true)
  }, [])

  return {
    canPromptNative: deferred !== null,
    isIos: isIosSafari(),
    standalone: isStandaloneDisplay(),
    dismissed,
    dismiss,
    promptInstall: showNativeInstallPrompt,
  }
}
