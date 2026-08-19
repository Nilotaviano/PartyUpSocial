import { useEffect, useSyncExternalStore } from 'react'
import { useTranslation } from 'react-i18next'
import { NoticeBanner } from '../../components/shell/NoticeBanner'
import { clearInstallAccepted, getInstallAccepted, subscribeToInstallAccepted } from './installPrompt'

const AUTO_CLEAR_MS = 8000

/**
 * D-220's accepted-install feedback. `showNativeInstallPrompt` is the single choke point both
 * `InstallBanner` and the AccountMenu row call through, so this live region — mounted once at the
 * app root alongside `OfflineBanner` — covers both without either caller knowing it exists.
 * Follows the OfflineBanner idiom: the `role="status"` element stays mounted and only its text
 * changes, so assistive tech that is already watching the region announces it reliably. Unlike
 * connectivity there is no natural "reversed" event to clear on, so the message clears itself
 * after a few seconds instead of waiting for one.
 */
export function InstallAcceptedNotice() {
  const { t } = useTranslation('pwa')
  const accepted = useSyncExternalStore(subscribeToInstallAccepted, getInstallAccepted, () => false)

  useEffect(() => {
    if (!accepted) return
    const timer = window.setTimeout(() => clearInstallAccepted(), AUTO_CLEAR_MS)
    return () => window.clearTimeout(timer)
  }, [accepted])

  return (
    <NoticeBanner
      className={
        accepted
          ? 'mx-auto w-full max-w-page px-4 pt-2 text-[13px] text-ink-soft sm:px-8'
          : 'sr-only'
      }
      title={accepted ? t('install.accepted') : ''}
      variant="status"
    />
  )
}
