import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import { Button } from '../../components/ui/button'
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
    return (
      <p className="mx-auto max-w-6xl px-4 py-2 text-ink-soft sm:px-8" role="status">
        {t('enable.fallback')}
      </p>
    )
  }

  if (asked || !supported) {
    return null
  }

  return (
    <section
      aria-labelledby="push-enable-heading"
      className="mx-auto mt-4 max-w-6xl rounded-card border-2 border-ink bg-card p-4 shadow-offset-card sm:mx-8"
      data-testid="push-enable-card"
    >
      <h2 className="font-display text-h3 font-semibold text-ink" id="push-enable-heading">
        {t('enable.title')}
      </h2>
      <p className="mt-1 text-ink-soft">{t('enable.body')}</p>
      {enable.isError && !(enable.error instanceof PushPermissionDeniedError) && (
        <p role="alert">{t('enable.error')}</p>
      )}
      <div className="mt-3 flex flex-wrap gap-2">
        <Button
          aria-busy={enable.isPending}
          disabled={enable.isPending}
          variant="primary"
          onClick={() => enable.mutate()}
        >
          {t('enable.confirm')}
        </Button>
        <Button disabled={enable.isPending} variant="neutral" onClick={dismiss}>
          {t('enable.dismiss')}
        </Button>
      </div>
    </section>
  )
}
