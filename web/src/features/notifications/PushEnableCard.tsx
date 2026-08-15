import { useMutation, useQuery } from '@tanstack/react-query'
import { Bell } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import { NoticeBanner } from '../../components/shell/NoticeBanner'
import { usePageSlot } from '../../components/ui/pageSlotContext'
import {
  PushPermissionDeniedError,
  enablePushInThisBrowser,
  hasAskedForPushInThisBrowser,
  isPushSupported,
  markPushAskedInThisBrowser,
} from './pushSubscription'

/**
 * PHASE7_DESIGN.md §5 (D-147/D-159/D-175), ruling 4. Mounted once in the authenticated chrome
 * (`App.tsx`'s `AuthenticatedShell`), so it evaluates on every authenticated page rather than only
 * one route. `pushPromptEligible` (a server fact, §9 ruling 4) and this-browser's asked state
 * (D-181 client-preference idiom, `pushSubscription.ts`) are independent axes — both must hold
 * before the card renders, and NEITHER axis alone ever triggers `Notification.requestPermission()`:
 * only the user's own click on "Enable" does (D-159 — no automatic prompt).
 */
export function PushEnableCard() {
  const { t } = useTranslation('notifications')
  const [asked, setAsked] = useState(() => hasAskedForPushInThisBrowser())
  const [showFallback, setShowFallback] = useState(false)
  // §3.5/R3: a migrated page offers a slot below its PageHeader and the card
  // portals into it; every other page (and every provider-less test) keeps the
  // shell position, so both arms — prompt and D-175 fallback — render wherever
  // this component happens to be mounted.
  const slot = usePageSlot()

  function place(content: ReactNode) {
    return slot ? createPortal(content, slot) : content
  }

  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const eligible = profile.data?.pushPromptEligible ?? false
  const supported = isPushSupported()

  // D-175: an unsupported browser (PushManager absent, iOS Safari included) is routed straight to
  // the fallback notice, marked asked so it never re-nags — the same one-time treatment an
  // explicit dismiss gets below, so this never becomes a persistent banner (MUST NOT: no re-nag).
  // React's own "adjusting state during render" idiom (`notificationQueries.ts`'s
  // `useNotificationScope` carries the same pattern) — never an effect: the `!showFallback` guard
  // makes this a one-time transition per mount, so it bails out on the very next render rather
  // than cascading.
  if (eligible && !asked && !supported && !showFallback) {
    markPushAskedInThisBrowser()
    setAsked(true)
    setShowFallback(true)
  }

  const enable = useMutation({
    mutationFn: enablePushInThisBrowser,
    onSuccess: () => {
      markPushAskedInThisBrowser()
      setAsked(true)
    },
    onError: (error) => {
      markPushAskedInThisBrowser()
      setAsked(true)
      if (error instanceof PushPermissionDeniedError) {
        setShowFallback(true)
      }
    },
  })

  function dismiss() {
    markPushAskedInThisBrowser()
    setAsked(true)
  }

  if (!eligible) {
    return null
  }

  if (showFallback) {
    return place(
      <NoticeBanner
        className={
          slot
            ? 'text-ink-soft'
            : 'mx-auto w-full max-w-page px-4 py-2 text-ink-soft sm:px-8'
        }
        title={t('enable.fallback')}
        variant="status"
      />,
    )
  }

  if (asked || !supported) {
    return null
  }

  return place(
    <div
      className={slot ? 'w-full' : 'mx-auto w-full max-w-page px-4 pt-4 sm:px-8'}
      data-testid="push-enable-card"
    >
      <NoticeBanner
        actionBusy={enable.isPending}
        actionLabel={t('enable.confirm')}
        body={t('enable.body')}
        dismissLabel={t('enable.dismiss')}
        icon={Bell}
        title={t('enable.title')}
        onAction={() => enable.mutate()}
        onDismiss={dismiss}
      />
      {enable.isError && !(enable.error instanceof PushPermissionDeniedError) && (
        <p className="mt-2 text-ink" role="alert">
          {t('enable.error')}
        </p>
      )}
    </div>,
  )
}
