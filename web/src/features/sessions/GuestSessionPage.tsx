import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import {
  claimGuestSession,
  getGuestSession,
  guestSessionKeys,
  guestTerminalLocaleKey,
  renameGuestSession,
  respondAsGuest,
  type GuestRsvpResponse,
} from './guestSessionQueries'

const rsvpChoices: GuestRsvpResponse[] = ['Going', 'Maybe', 'Declined']

/**
 * design §10.1: the single component behind both guest routes. Mounted at
 * `/guest/:guestLinkId` it renders live/terminal state from
 * `guestSessionQueries`; mounted at `/guest/invalid` (no `guestLinkId` param)
 * it renders the invalid-link notice with no query and no cookie read.
 */
export function GuestSessionPage() {
  const { t, i18n } = useTranslation(['sessions', 'errors', 'common'])
  const { guestLinkId } = useParams()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [claimResponse, setClaimResponse] = useState<GuestRsvpResponse | ''>('')
  const renameInput = useRef<HTMLInputElement>(null)

  const detail = useQuery({
    queryKey: guestSessionKeys.detail(guestLinkId ?? ''),
    queryFn: () => getGuestSession(guestLinkId!),
    enabled: Boolean(guestLinkId),
    retry: false,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [guestLinkId])

  async function afterMutation(message: string) {
    setStatus(message)
    await queryClient.invalidateQueries({
      queryKey: guestSessionKeys.detail(guestLinkId ?? ''),
    })
  }

  const claim = useMutation({
    mutationFn: () =>
      claimGuestSession(guestLinkId!, displayName, claimResponse || 'Pending'),
    onSuccess: () => afterMutation(t('rsvp.updated')),
  })

  const rename = useMutation({
    mutationFn: (value: string) => renameGuestSession(guestLinkId!, value),
    onSuccess: () => afterMutation(t('rsvp.updated')),
  })

  const respond = useMutation({
    mutationFn: (response: GuestRsvpResponse) =>
      respondAsGuest(guestLinkId!, response),
    onSuccess: () => afterMutation(t('rsvp.updated')),
  })

  function renderInvalid(extra?: unknown) {
    return (
      <main
        className="mx-auto max-w-xl space-y-4 p-4 sm:p-8"
        data-testid="screen-guest-invalid"
      >
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-2xl font-bold tracking-tight text-slate-950 focus:outline-none"
        >
          {t('guest.invalid')}
        </h1>
        <p>{t('guest.linkExpiredReopen')}</p>
        {extra != null && isApiError(extra) && extra.code !== 'session_not_found' && (
          <p role="alert">
            {t(extra.code, { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) })}
          </p>
        )}
      </main>
    )
  }

  // The invalid route carries no link id: no query, no cookie read (design §10.1).
  if (!guestLinkId) {
    return renderInvalid()
  }

  if (detail.isError) {
    return renderInvalid(detail.error)
  }

  if (!detail.data) {
    return (
      <main
        className="mx-auto max-w-xl space-y-4 p-4 sm:p-8"
        data-testid="screen-guest-session"
      >
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-2xl font-bold tracking-tight text-slate-950 focus:outline-none"
        >
          {t('common.loading', { ns: 'common' })}
        </h1>
      </main>
    )
  }

  const view = detail.data

  if (view.state !== 'live') {
    return (
      <main
        className="mx-auto max-w-xl space-y-4 p-4 sm:p-8"
        data-testid="screen-guest-terminal"
      >
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-2xl font-bold tracking-tight text-slate-950 focus:outline-none"
        >
          {t(guestTerminalLocaleKey(view.state))}
        </h1>
      </main>
    )
  }

  const mutationError = claim.error ?? rename.error ?? respond.error
  const hasClaimed = Boolean(view.guestIdentityId)

  return (
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-guest-session"
    >
      <h1
        ref={heading}
        aria-live="polite"
        tabIndex={-1}
        className="text-2xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {view.title ?? t('guest.claimHeading')}
      </h1>

      <p aria-live="polite" className="text-sm text-emerald-700">
        {status}
      </p>

      {mutationError && (
        <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
          {t(
            isApiError(mutationError)
              ? `errors.codes.${mutationError.code}`
              : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      <p className="text-sm text-slate-600">{t('guest.live')}</p>

      {view.startTime && (
        <p>
          {new Intl.DateTimeFormat(i18n.language, {
            dateStyle: 'medium',
            timeStyle: 'short',
          }).format(new Date(view.startTime))}
        </p>
      )}
      <p className="text-xs text-slate-500">{t('guest.timezone')}</p>

      {view.platform && <p>{view.platform}</p>}
      {view.game?.name && <p>{view.game.name}</p>}
      {view.notes && <p>{view.notes}</p>}

      {!hasClaimed && (
        <form
          className="space-y-3"
          onSubmit={(event) => {
            event.preventDefault()
            claim.mutate()
          }}
        >
          <h2 className="text-lg font-semibold text-slate-950">
            {t('guest.claimHeading')}
          </h2>
          <div>
            <Label htmlFor="guest-display-name">{t('guest.displayName')}</Label>
            <Input
              id="guest-display-name"
              required
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
            />
          </div>
          <fieldset>
            <legend>{t('rsvp.heading')}</legend>
            {rsvpChoices.map((choice) => (
              <label key={choice} className="mr-3 inline-flex items-center gap-2">
                <input
                  checked={claimResponse === choice}
                  name="claim-response"
                  type="radio"
                  onChange={() => setClaimResponse(choice)}
                />
                {t(`rsvp.${choice.toLowerCase()}`)}
              </label>
            ))}
          </fieldset>
          <Button
            aria-busy={claim.isPending}
            className="min-h-11 rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            disabled={claim.isPending}
            type="submit"
          >
            {t('guest.respond')}
          </Button>
        </form>
      )}

      {hasClaimed && (
        <>
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(event) => {
              event.preventDefault()
              rename.mutate(renameInput.current?.value ?? '')
            }}
          >
            <div>
              <Label htmlFor="guest-rename">{t('guest.displayName')}</Label>
              <input
                key={view.guestIdentityId}
                ref={renameInput}
                className="min-h-11 rounded-md border border-slate-300 px-3 py-2"
                defaultValue={view.displayName ?? ''}
                id="guest-rename"
                required
              />
            </div>
            <Button
              aria-busy={rename.isPending}
              className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              disabled={rename.isPending}
              type="submit"
            >
              {t('guest.respond')}
            </Button>
          </form>

          <div className="flex flex-wrap gap-2">
            {rsvpChoices.map((choice) => (
              <button
                key={choice}
                aria-pressed={view.response?.toLowerCase() === choice.toLowerCase()}
                className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                disabled={respond.isPending}
                type="button"
                onClick={() => respond.mutate(choice)}
              >
                {t(`rsvp.${choice.toLowerCase()}`)}
              </button>
            ))}
          </div>
        </>
      )}
    </main>
  )
}
