import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { sessionQueryKey, useSession } from '../../auth/useSession'
import { isKnownTimeZone } from '../../components/TimeZoneField'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { useToast } from '../../components/ui/toastStore'
import { hasDismissedTimezoneMismatch, markTimezoneMismatchDismissed } from './timezoneMismatchDismissal'

function detectedBrowserTimeZone(): string | null {
  try {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    return zone && isKnownTimeZone(zone) ? zone : null
  } catch {
    return null
  }
}

/**
 * D-197: mounted once in the authenticated chrome (`App.tsx`'s `AuthenticatedShell`), the same
 * placement `PushEnableCard` uses — that placement alone already keeps this out of onboarding (the
 * onboarding routes render outside `RequireOnboarded`/`AuthenticatedShell`) and away from guests
 * (never authenticated, never routed under `RequireAuth`). `needsOnboarding` below mirrors
 * `RequireOnboarded`'s own guard as a second, direct check so the "never during onboarding" rule
 * holds even if this component is ever mounted somewhere else.
 */
export function TimezoneMismatchDialog() {
  const { t } = useTranslation(['settings', 'errors'])
  const queryClient = useQueryClient()
  const showToast = useToast()
  const { data: session, isPending } = useSession()
  // React's own "adjusting state during render" idiom (`PushEnableCard`'s own `showFallback`
  // transition carries the same pattern) — never an effect: `promptedPair` records which pair this
  // render cycle has already decided to show, so the render below bails out on the very next pass
  // rather than cascading, and a NEW pair (promptedPair !== pairToken) is free to prompt again even
  // right after a prior pair was closed.
  const [promptedPair, setPromptedPair] = useState<string | null>(null)
  const [closed, setClosed] = useState(false)

  const profileZone = session?.profile?.timeZone ?? null
  const browserZone = detectedBrowserTimeZone()
  const authenticated = session?.authenticated === true
  const needsOnboarding = session ? session.state === 'onboarding' || session.onboarding !== null : true
  const pairToken = profileZone !== null && browserZone !== null ? `${profileZone}|${browserZone}` : null

  const mismatch =
    !isPending &&
    authenticated &&
    !needsOnboarding &&
    profileZone !== null &&
    browserZone !== null &&
    profileZone !== browserZone

  if (
    mismatch &&
    pairToken !== null &&
    pairToken !== promptedPair &&
    !hasDismissedTimezoneMismatch(profileZone!, browserZone!)
  ) {
    setPromptedPair(pairToken)
    setClosed(false)
  }

  const open = mismatch && pairToken !== null && pairToken === promptedPair && !closed

  const update = useMutation({
    mutationFn: async (timeZone: string) => {
      await client.PUT('/api/v1/me/time-zone', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { timeZone },
      })
    },
    onSuccess: async (_data, timeZone) => {
      if (profileZone) markTimezoneMismatchDismissed(profileZone, timeZone)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      setClosed(true)
      showToast({ message: t('timezoneMismatch.updated', { zone: timeZone }) })
    },
  })

  function decline() {
    if (profileZone && browserZone) markTimezoneMismatchDismissed(profileZone, browserZone)
    setClosed(true)
  }

  if (!mismatch || !profileZone || !browserZone) {
    return null
  }

  const errorMessage = update.isError
    ? isApiError(update.error)
      ? t(update.error.code, { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) })
      : t('generic', { ns: 'errors' })
    : null

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) decline()
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-busy={update.isPending || undefined}
          aria-describedby="tz-mismatch-description"
          aria-label={t('timezoneMismatch.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('timezoneMismatch.title')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="tz-mismatch-description">
            {t('timezoneMismatch.body', { browserZone, profileZone })}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Button
              disabled={update.isPending}
              type="button"
              variant="neutral"
              onClick={decline}
            >
              {t('timezoneMismatch.keep', { zone: profileZone })}
            </Button>
            <Button
              disabled={update.isPending}
              type="button"
              variant="primary"
              onClick={() => update.mutate(browserZone)}
            >
              {t('timezoneMismatch.update', { zone: browserZone })}
            </Button>
          </div>
          {errorMessage && (
            <p className="mt-4 text-danger" role="alert">
              {errorMessage}
            </p>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
