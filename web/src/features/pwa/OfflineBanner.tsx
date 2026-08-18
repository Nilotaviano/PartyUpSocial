import { useSyncExternalStore } from 'react'
import { useTranslation } from 'react-i18next'
import { NoticeBanner } from '../../components/shell/NoticeBanner'

function subscribeToConnectivity(onStoreChange: () => void) {
  window.addEventListener('online', onStoreChange)
  window.addEventListener('offline', onStoreChange)
  return () => {
    window.removeEventListener('online', onStoreChange)
    window.removeEventListener('offline', onStoreChange)
  }
}

function isOnline() {
  return typeof navigator === 'undefined' ? true : navigator.onLine
}

/**
 * D-219's connectivity notice. The live region is always mounted and only its text changes:
 * a `role="status"` element that appears and disappears is announced unreliably, whereas text
 * arriving in a region the assistive tech is already watching is announced on both edges — going
 * offline, and again silent when the connection returns.
 */
export function OfflineBanner() {
  const { t } = useTranslation('pwa')
  const online = useSyncExternalStore(subscribeToConnectivity, isOnline, () => true)

  return (
    <NoticeBanner
      className={
        online
          ? 'sr-only'
          : 'mx-auto w-full max-w-page px-4 pt-2 text-[13px] text-ink-soft sm:px-8'
      }
      title={online ? '' : t('offline.title')}
      variant="status"
    />
  )
}
