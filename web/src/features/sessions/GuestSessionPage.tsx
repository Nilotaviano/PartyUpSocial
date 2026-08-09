import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import {
  claimGuestPoll,
  getGuestPoll,
  guestPollKeys,
  guestPollTerminalLocaleKey,
  renameGuestPollIdentity,
  suggestPollOptionAsGuest,
  voteOnPollAsGuest,
} from '../polls/guestPollQueries'
import { VotingGrid, type VoteChoiceValue } from '../polls/VotingGrid'
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
const MAX_OPTIONS = 12

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

  // A session-typed and a poll-typed guest link share one route and one cookie name; the
  // credential's purpose decides which resource resolves (§4/A-015). The bearer carries no
  // signal of which kind it is, so this probes session first and falls back to poll only once
  // the session read structurally 404s (poll-typed cookies never decrypt under the session
  // protector, and vice versa — PHASE6_DESIGN.md §4).
  const pollDetail = useQuery({
    queryKey: guestPollKeys.detail(guestLinkId ?? ''),
    queryFn: () => getGuestPoll(guestLinkId!),
    enabled: Boolean(guestLinkId) && detail.isError,
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
          className="text-h1 font-display font-bold text-ink focus:outline-none"
        >
          {t('guest.invalid')}
        </h1>
        <p className="text-ink-soft">{t('guest.linkExpiredReopen')}</p>
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
    if (pollDetail.data) {
      return <GuestPollBranch guestLinkId={guestLinkId} heading={heading} view={pollDetail.data} />
    }
    if (pollDetail.isError) {
      return renderInvalid(pollDetail.error)
    }
    return (
      <main
        className="mx-auto max-w-xl space-y-4 p-4 sm:p-8"
        data-testid="screen-guest-session"
      >
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-h1 font-display font-bold text-ink focus:outline-none"
        >
          {t('common.loading', { ns: 'common' })}
        </h1>
      </main>
    )
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
          className="text-h1 font-display font-bold text-ink focus:outline-none"
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
          className="text-h1 font-display font-bold text-ink focus:outline-none"
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
        className="text-h1 font-display font-bold text-ink focus:outline-none"
      >
        {view.title ?? t('guest.claimHeading')}
      </h1>

      <p aria-live="polite" className="text-body text-ink">
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

      <p className="text-ink-soft">{t('guest.live')}</p>

      {view.startTime && (
        <p>
          {new Intl.DateTimeFormat(i18n.language, {
            dateStyle: 'medium',
            timeStyle: 'short',
          }).format(new Date(view.startTime))}
        </p>
      )}
      <p className="text-meta text-ink-soft">{t('guest.timezone')}</p>

      {view.platform && <p>{view.platform}</p>}
      {view.game?.name && <p>{view.game.name}</p>}
      {view.notes && <p>{view.notes}</p>}

      {!hasClaimed && (
        <form
          className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
          onSubmit={(event) => {
            event.preventDefault()
            claim.mutate()
          }}
        >
          <h2 className="text-h2 font-display font-semibold text-ink">
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
          <Button aria-busy={claim.isPending} disabled={claim.isPending} type="submit" variant="primary">
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
                className="min-h-11 rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                defaultValue={view.displayName ?? ''}
                id="guest-rename"
                required
              />
            </div>
            <Button aria-busy={rename.isPending} disabled={rename.isPending} type="submit" variant="primary">
              {t('guest.respond')}
            </Button>
          </form>

          <div className="flex flex-wrap gap-2">
            {rsvpChoices.map((choice) => {
              const pressed = view.response?.toLowerCase() === choice.toLowerCase()
              return (
                <Button
                  key={choice}
                  aria-pressed={pressed}
                  disabled={respond.isPending}
                  type="button"
                  variant={pressed ? 'primary' : 'neutral'}
                  onClick={() => respond.mutate(choice)}
                >
                  {t(`rsvp.${choice.toLowerCase()}`)}
                </Button>
              )
            })}
          </div>
        </>
      )}
    </main>
  )
}

type GuestPollViewData = Awaited<ReturnType<typeof getGuestPoll>>

type GuestPollBranchProps = {
  guestLinkId: string
  heading: React.RefObject<HTMLHeadingElement | null>
  view: GuestPollViewData
}

/**
 * design §5.4/§5 pollView branch: explicit claim with display-name capture (D-017), the shared
 * voting grid over the guest transport, suggest (D-033) and self-rename (D-044 guest arm).
 * Guests read the masked grid before claiming — voting itself 409s unclaimed at the application
 * layer, so the grid stays present but disabled until claim.
 */
function GuestPollBranch({ guestLinkId, heading, view }: GuestPollBranchProps) {
  const { t, i18n } = useTranslation(['polls', 'common'])
  const queryClient = useQueryClient()
  const [status, setStatus] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [pendingOptionId, setPendingOptionId] = useState<string | null>(null)
  const [suggestValue, setSuggestValue] = useState('')
  const [suggestError, setSuggestError] = useState<string | null>(null)
  const renameInput = useRef<HTMLInputElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [heading])

  async function afterMutation(message: string) {
    setStatus(message)
    setPendingOptionId(null)
    await queryClient.invalidateQueries({ queryKey: guestPollKeys.detail(guestLinkId) })
  }

  const claim = useMutation({
    mutationFn: () => claimGuestPoll(guestLinkId, displayName),
    onSuccess: () => afterMutation(t('vote.updated')),
  })

  const rename = useMutation({
    mutationFn: (value: string) => renameGuestPollIdentity(guestLinkId, value),
    onSuccess: () => afterMutation(t('vote.updated')),
  })

  const vote = useMutation({
    mutationFn: ({ optionId, choice }: { optionId: string; choice: VoteChoiceValue }) => {
      setPendingOptionId(optionId)
      return voteOnPollAsGuest(guestLinkId, optionId, choice)
    },
    onSuccess: () => afterMutation(t('vote.updated')),
    onError: () => setPendingOptionId(null),
  })

  const suggest = useMutation({
    mutationFn: (startTime: string) => suggestPollOptionAsGuest(guestLinkId, startTime),
    onSuccess: () => {
      setSuggestValue('')
      return afterMutation(t('suggest.added'))
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'poll_option_cap') {
        setSuggestError(t('suggest.optionCap'))
      }
    },
  })

  if (view.state !== 'live') {
    return (
      <main className="mx-auto max-w-xl space-y-4 p-4 sm:p-8" data-testid="screen-guest-terminal">
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-h1 font-display font-bold text-ink focus:outline-none"
        >
          {t(guestPollTerminalLocaleKey(view.state))}
        </h1>
      </main>
    )
  }

  const hasClaimed = Boolean(view.guestIdentityId)
  const mutationError = claim.error ?? rename.error ?? vote.error ?? suggest.error

  function handleSuggest(event: React.FormEvent) {
    event.preventDefault()
    setSuggestError(null)
    if (!suggestValue) return
    const asInstant = new Date(suggestValue)
    if (Number.isNaN(asInstant.getTime())) return
    if ((view.options?.length ?? 0) >= MAX_OPTIONS) {
      setSuggestError(t('suggest.optionCap'))
      return
    }
    suggest.mutate(asInstant.toISOString())
  }

  return (
    <main className="mx-auto max-w-xl space-y-6 p-4 sm:p-8" data-testid="screen-guest-poll">
      <h1
        ref={heading}
        aria-live="polite"
        tabIndex={-1}
        className="text-h1 font-display font-bold text-ink focus:outline-none"
      >
        {view.title ?? t('guestPoll.claimHeading')}
      </h1>

      <p aria-live="polite" className="text-body text-ink">
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

      <p className="text-ink-soft">{t('guestPoll.live')}</p>
      {view.notes && <p>{view.notes}</p>}

      {!hasClaimed && (
        <form
          className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
          onSubmit={(event) => {
            event.preventDefault()
            claim.mutate()
          }}
        >
          <h2 className="text-h2 font-display font-semibold text-ink">
            {t('guestPoll.claimHeading')}
          </h2>
          <div>
            <Label htmlFor="guest-poll-display-name">{t('guestPoll.displayName')}</Label>
            <Input
              id="guest-poll-display-name"
              required
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
            />
          </div>
          <Button aria-busy={claim.isPending} disabled={claim.isPending} type="submit" variant="primary">
            {t('guestPoll.join')}
          </Button>
        </form>
      )}

      {hasClaimed && (
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(event) => {
            event.preventDefault()
            rename.mutate(renameInput.current?.value ?? '')
          }}
        >
          <div>
            <Label htmlFor="guest-poll-rename">{t('guestPoll.displayName')}</Label>
            <input
              key={view.guestIdentityId}
              ref={renameInput}
              className="min-h-11 rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
              defaultValue={view.displayName ?? ''}
              id="guest-poll-rename"
              required
            />
          </div>
          <Button aria-busy={rename.isPending} disabled={rename.isPending} type="submit" variant="primary">
            {t('guestPoll.join')}
          </Button>
        </form>
      )}

      <section aria-labelledby="guest-poll-options-heading">
        <h2 id="guest-poll-options-heading" className="text-h2 font-display font-semibold text-ink">
          {t('detail.options')}
        </h2>
        <VotingGrid
          disabled={!hasClaimed}
          locale={i18n.language}
          options={view.options ?? []}
          pendingOptionId={pendingOptionId}
          onVote={(optionId, choice) => vote.mutate({ optionId, choice })}
        />

        {hasClaimed && (
          <form className="mt-4 flex flex-wrap items-end gap-2" onSubmit={handleSuggest}>
            <div className="flex-1">
              <label
                className="block font-body font-semibold text-ink"
                htmlFor="guest-suggest-option-value"
              >
                {t('suggest.heading')}
              </label>
              <input
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="guest-suggest-option-value"
                step={900}
                type="datetime-local"
                value={suggestValue}
                onChange={(event) => setSuggestValue(event.target.value)}
              />
            </div>
            <Button aria-busy={suggest.isPending} disabled={suggest.isPending} type="submit" variant="neutral">
              {t('suggest.add')}
            </Button>
          </form>
        )}
        {suggestError && <p role="alert">{suggestError}</p>}
      </section>
    </main>
  )
}
