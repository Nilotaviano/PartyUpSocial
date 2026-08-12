import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { useSession } from '../../auth/useSession'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import {
  cancelPoll,
  createPollGuestLinks,
  finalizePoll,
  getPollDetail,
  getPollFinalizationPreview,
  leavePoll,
  mutePoll,
  pollKeys,
  pollWindowState,
  removePollGuest,
  revokePollGuestLink,
  suggestPollOption,
  unmutePoll,
  voteOnPollOption,
  type FinalizationPreviewOption,
  type GuestLinkInvitation,
  type PollDetail,
} from './pollQueries'
import { PollStatusChip } from './PollStatusChip'
import { VotingGrid, type VoteChoiceValue } from './VotingGrid'

// D-080/D-171 (suggestion-time cap): the same 12-option ceiling as creation, checked
// client-side purely for the inline UX — the server re-checks and returns poll_option_cap,
// which this arm renders with its OWN copy (distinct from the creation-cap D-131 string).
const MAX_OPTIONS = 12

type RemoveGuestDialogProps = {
  displayName: string
  pending: boolean
  onConfirm: () => void
}

function RemoveGuestDialog({ displayName, pending, onConfirm }: RemoveGuestDialogProps) {
  const { t } = useTranslation('polls')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-label={`${t('guests.remove')} ${displayName}`}
          disabled={pending}
          variant="danger"
        >
          {t('guests.remove')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-remove-guest-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('guests.remove')}
          </Dialog.Title>
          <Dialog.Description
            id="poll-remove-guest-description"
            className="mt-2 text-ink-soft"
          >
            {t('guests.removeConfirm')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('guests.remove')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type CancelPollDialogProps = {
  pending: boolean
  onConfirm: () => void
}

/**
 * D-156: the organizer may cancel any time before finalization. Mirrors
 * SessionDetailPage's CancelSessionDialog exactly — same Dialog structure, focus handling
 * and danger variants. Guest links are revoked server-side on cancel (D-167/D-174), which is
 * why the body copy below says so rather than reading as a reversible soft toggle.
 */
function CancelPollDialog({ pending, onConfirm }: CancelPollDialogProps) {
  const { t } = useTranslation('polls')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button disabled={pending} variant="danger">
          {t('cancel.action')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-cancel-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('cancel.title')}
          </Dialog.Title>
          <Dialog.Description id="poll-cancel-description" className="mt-2 text-ink-soft">
            {t('cancel.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('cancel.confirm')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type GuestPanelProps = {
  pollId: string
  roster: PollDetail['roster']
  canMint: boolean
  onError: (error: unknown) => void
  clearError: () => void
}

/**
 * design §5/§6.1 guest-management panel — SessionInvitePage's mint/copy/revoke idiom, plus
 * D-044 remove (the only detach for a claimed guest; renaming stays with the guest). Minted
 * links are plaintext-shown-once local state, matching the shipped session precedent — there
 * is no list-unclaimed-links endpoint to hydrate from.
 */
function GuestPanel({ pollId, roster, canMint, onError, clearError }: GuestPanelProps) {
  const { t } = useTranslation('polls')
  const queryClient = useQueryClient()
  const [linkCount, setLinkCount] = useState('1')
  const [guestLinks, setGuestLinks] = useState<GuestLinkInvitation[]>([])

  const mint = useMutation({
    mutationFn: () => createPollGuestLinks(pollId, Number(linkCount)),
    onSuccess: (created) => {
      clearError()
      setGuestLinks((current) => [...current, ...created])
    },
    onError,
  })

  // D-044: a claimed link's revoke 409s (poll_conflict) — the link stays listed on failure
  // (never removed from local state here) and the specific "remove the guest instead" copy
  // renders through the shared error-mapping idiom (PollDetailPage's own alert block).
  const revoke = useMutation({
    mutationFn: (guestLinkId: string) => revokePollGuestLink(pollId, guestLinkId),
    onSuccess: (_result, guestLinkId) => {
      clearError()
      setGuestLinks((current) => current.filter((link) => link.guestLinkId !== guestLinkId))
    },
    onError,
  })

  const remove = useMutation({
    mutationFn: (guestIdentityId: string) => removePollGuest(pollId, guestIdentityId),
    onSuccess: () => {
      clearError()
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
    onError,
  })

  async function copy(url: string) {
    await navigator.clipboard.writeText(url)
  }

  const claimedGuests = roster.filter((entry) => entry.guestIdentityId && entry.isActive)

  return (
    <section aria-labelledby="poll-guests-heading">
      <h2 id="poll-guests-heading" className="text-h2 font-display font-semibold text-ink">
        {t('guests.heading')}
      </h2>

      {canMint && (
        <>
          <label className="mt-2 block font-body font-semibold text-ink" htmlFor="poll-guest-link-count">
            {t('create.guestLinks')}
          </label>
          <input
            className="mt-1 min-h-11 w-24 rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            id="poll-guest-link-count"
            max={20}
            min={1}
            type="number"
            value={linkCount}
            onChange={(event) => setLinkCount(event.target.value)}
          />
          <Button
            aria-busy={mint.isPending}
            className="ml-2"
            disabled={mint.isPending}
            variant="neutral"
            onClick={() => mint.mutate()}
          >
            {t('guests.mint')}
          </Button>
        </>
      )}

      {guestLinks.length > 0 && (
        <ul className="mt-3 space-y-2">
          {guestLinks.map((link) => (
            <li
              key={link.guestLinkId}
              className="flex items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-3"
            >
              <span className="min-w-0 flex-1 truncate font-mono text-ink">{link.url}</span>
              <span className="flex flex-none gap-2">
                <Button
                  aria-label={t('a11y.copyLink')}
                  type="button"
                  variant="ghost"
                  onClick={() => void copy(link.url)}
                >
                  {t('guests.copyLink')}
                </Button>
                <Button
                  disabled={revoke.isPending}
                  type="button"
                  variant="danger"
                  onClick={() => revoke.mutate(link.guestLinkId)}
                >
                  {t('guests.revoke')}
                </Button>
              </span>
            </li>
          ))}
        </ul>
      )}

      {claimedGuests.length > 0 && (
        <ul className="mt-3 space-y-1">
          {claimedGuests.map((entry) => (
            <li
              key={entry.participationId}
              className="flex items-center justify-between gap-2 py-1"
            >
              <span className="text-ink">{entry.displayName ?? t('detail.anonymous')}</span>
              <RemoveGuestDialog
                displayName={entry.displayName ?? t('detail.anonymous')}
                pending={remove.isPending}
                onConfirm={() => remove.mutate(entry.guestIdentityId as string)}
              />
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

type FinalizeDialogProps = {
  option: FinalizationPreviewOption
  locale: string
  early: boolean
  pending: boolean
  onConfirm: () => void
}

/**
 * design §5 (kit confirm Dialog): finalize is a destructive, irreversible transition — one option
 * is selected and confirmed behind the shipped Dialog idiom before the mutation fires. Before the
 * deadline (D-187) the confirmation also says what else it does: voting stops there and then.
 */
function FinalizeDialog({ option, locale, early, pending, onConfirm }: FinalizeDialogProps) {
  const { t } = useTranslation('polls')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const optionText = new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(option.startTime))

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-busy={pending}
          data-testid="finalize-trigger"
          disabled={pending}
          variant="primary"
        >
          {t('finalize.action')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-finalize-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t(early ? 'finalize.confirmTitleEarly' : 'finalize.confirmTitle')}
          </Dialog.Title>
          <Dialog.Description id="poll-finalize-description" className="mt-2 text-ink-soft">
            {t(early ? 'finalize.confirmBodyEarly' : 'finalize.confirmBody', { time: optionText })}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button
              data-testid="finalize-confirm"
              type="button"
              variant="primary"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('finalize.action')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type FinalizeSectionProps = {
  pollId: string
  locale: string
  early: boolean
  onError: (error: unknown) => void
}

/**
 * design §5/§6.1 awaitingSelection organizer controls — a per-option preview from
 * `GET /{id}/finalization-preview` (mapped roster, waitlist split, D-119 order preserved exactly
 * as the server returns it, never client-resorted), finalize behind a confirm Dialog, and the
 * exact preview-guarantee copy: preview and finalization share one mapping/allocation function —
 * identical inputs give identical outputs; a shown preview can still differ from the outcome when
 * inputs legitimately change in between. No stronger claim is made anywhere in this UI.
 *
 * `early` is the D-187 entry: the poll is still Polling, so only options the server already reports
 * as `meetsMinimum` may be selected, and selecting one ends voting there and then.
 */
function FinalizeSection({ pollId, locale, early, onError }: FinalizeSectionProps) {
  const { t } = useTranslation('polls')
  const navigate = useNavigate()
  const [selectedOptionId, setSelectedOptionId] = useState<string | null>(null)

  const preview = useQuery({
    queryKey: pollKeys.finalizationPreview(pollId),
    queryFn: () => getPollFinalizationPreview(pollId),
    retry: false,
  })

  const finalize = useMutation({
    mutationFn: (optionId: string) => finalizePoll(pollId, optionId),
    onSuccess: (response) => navigate(`/sessions/${response.sessionId}`),
    onError,
  })

  // D-187: while the poll is still Polling only a viable option may be selected, so the others are
  // not offered at all; with none viable there is no early finalization to offer yet.
  const options = (preview.data?.options ?? []).filter((option) => !early || option.meetsMinimum)
  const selected = options.find((option) => option.optionId === selectedOptionId) ?? null

  // Nothing to show until the preview says an option is viable — no loading shell, no error banner
  // on a page whose main business is still voting.
  if (early && !(preview.isSuccess && options.length > 0)) {
    return null
  }

  if (preview.isError) {
    return (
      <section aria-labelledby="poll-finalize-heading">
        <h2 id="poll-finalize-heading" className="text-h2 font-display font-semibold text-ink">
          {t('finalize.heading')}
        </h2>
        <p role="alert">
          {t(
            isApiError(preview.error)
              ? `errors.codes.${preview.error.code}`
              : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      </section>
    )
  }

  return (
    <section aria-labelledby="poll-finalize-heading">
      <h2 id="poll-finalize-heading" className="text-h2 font-display font-semibold text-ink">
        {t(early ? 'finalize.earlyHeading' : 'finalize.heading')}
      </h2>
      {early && (
        <p className="text-ink" data-testid="finalize-early-notice">
          {t('finalize.earlyNotice')}
        </p>
      )}
      <p className="text-ink-soft">{t('finalize.previewGuarantee')}</p>

      {preview.isPending && <p role="status">{t('common.loading', { ns: 'common' })}</p>}

      {options.length > 0 && (
        <fieldset className="mt-3 space-y-3">
          <legend className="sr-only">{t('finalize.chooseOption')}</legend>
          {options.map((option) => {
            const optionText = new Intl.DateTimeFormat(locale, {
              dateStyle: 'medium',
              timeStyle: 'short',
            }).format(new Date(option.startTime))
            const going = option.roster.filter((entry) => !entry.isWaitlisted)
            const waitlisted = option.roster.filter((entry) => entry.isWaitlisted)
            return (
              <label
                key={option.optionId}
                className="block rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
                data-testid={`finalize-option-${option.optionId}`}
              >
                <span className="flex items-center gap-2 font-display font-semibold text-ink">
                  <input
                    checked={selectedOptionId === option.optionId}
                    name="finalize-option"
                    type="radio"
                    value={option.optionId}
                    onChange={() => setSelectedOptionId(option.optionId)}
                  />
                  {optionText}
                  {!option.meetsMinimum && (
                    <span className="text-danger">{t('finalize.belowMinimum')}</span>
                  )}
                </span>
                {/* D-119 order: the server's own ordering, never re-sorted client-side. */}
                {going.length > 0 && (
                  <>
                    {/*
                      AVATARS_DESIGN.md §3.5: the discs are aria-hidden decoration rendered
                      alongside the name text below, never inside it — the radio's accessible
                      name still comes only from the comma-joined name string, unchanged from
                      wave A (_D188).
                    */}
                    <span aria-hidden="true" className="mt-1 flex flex-wrap gap-1">
                      {going.map((entry, index) => (
                        <AvatarDisc
                          key={entry.userId ?? entry.guestIdentityId ?? index}
                          emoji={entry.avatar?.emoji}
                          imgUrl={avatarImgUrl(entry.avatar)}
                          masked={entry.displayName === null}
                          seed={entry.userId ?? entry.guestIdentityId ?? String(index)}
                          size="xs"
                        />
                      ))}
                    </span>
                    <p className="mt-1 text-ink-soft">
                      {t('finalize.going', {
                        names: going
                          .map((entry) => entry.displayName ?? t('detail.anonymous'))
                          .join(', '),
                      })}
                    </p>
                  </>
                )}
                {waitlisted.length > 0 && (
                  <>
                    <span aria-hidden="true" className="mt-1 flex flex-wrap gap-1">
                      {waitlisted.map((entry, index) => (
                        <AvatarDisc
                          key={entry.userId ?? entry.guestIdentityId ?? index}
                          emoji={entry.avatar?.emoji}
                          imgUrl={avatarImgUrl(entry.avatar)}
                          masked={entry.displayName === null}
                          seed={entry.userId ?? entry.guestIdentityId ?? String(index)}
                          size="xs"
                        />
                      ))}
                    </span>
                    <p className="text-ink-soft">
                      {t('finalize.waitlisted', {
                        names: waitlisted
                          .map((entry) => entry.displayName ?? t('detail.anonymous'))
                          .join(', '),
                      })}
                    </p>
                  </>
                )}
              </label>
            )
          })}
        </fieldset>
      )}

      {selected && (
        <div className="mt-3">
          <FinalizeDialog
            early={early}
            locale={locale}
            option={selected}
            pending={finalize.isPending}
            onConfirm={() => finalize.mutate(selected.optionId)}
          />
        </div>
      )}
    </section>
  )
}

/**
 * Poll detail (S6 scope): the live voting grid, suggest-option affordance and the organizer's
 * guest-management panel join S5's read-only shell (design §5/§6).
 */
export function PollDetailPage() {
  const { t, i18n } = useTranslation(['polls', 'errors', 'common'])
  const { pollId = '' } = useParams()
  const { data: session } = useSession()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const [pendingOptionId, setPendingOptionId] = useState<string | null>(null)
  const [suggestValue, setSuggestValue] = useState('')
  const [suggestError, setSuggestError] = useState<string | null>(null)
  const [suggestNotice, setSuggestNotice] = useState<string | null>(null)
  const [guestPanelError, setGuestPanelError] = useState<unknown>(null)
  const [finalizeError, setFinalizeError] = useState<unknown>(null)
  const [cancelError, setCancelError] = useState<unknown>(null)

  const detail = useQuery({
    queryKey: pollKeys.detail(pollId),
    queryFn: () => getPollDetail(pollId),
    enabled: Boolean(pollId),
    retry: false,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  async function afterVote(message: string) {
    setStatus(message)
    setPendingOptionId(null)
    await queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    // A vote is what moves an option over the D-187 line, so the organizer's preview goes stale.
    await queryClient.invalidateQueries({ queryKey: pollKeys.finalizationPreview(pollId) })
  }

  const vote = useMutation({
    mutationFn: ({ optionId, choice }: { optionId: string; choice: VoteChoiceValue }) => {
      setPendingOptionId(optionId)
      return voteOnPollOption(pollId, optionId, choice)
    },
    onSuccess: () => afterVote(t('vote.updated')),
    onError: () => setPendingOptionId(null),
  })

  const suggest = useMutation({
    mutationFn: (startTime: string) => suggestPollOption(pollId, startTime),
    onSuccess: () => {
      setSuggestValue('')
      setSuggestNotice(t('suggest.added'))
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'poll_option_cap') {
        setSuggestError(t('suggest.optionCap'))
      }
    },
  })

  const mute = useMutation({
    mutationFn: async () => {
      const wasMuted = detail.data?.muted ?? false
      if (wasMuted) {
        await unmutePoll(pollId)
      } else {
        await mutePoll(pollId)
      }
      return !wasMuted
    },
    onSuccess: (nowMuted) => {
      setStatus(nowMuted ? t('mute.mutedAnnouncement') : t('mute.unmutedAnnouncement'))
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
  })

  const cancel = useMutation({
    mutationFn: () => cancelPoll(pollId),
    onError: setCancelError,
  })

  // D-186. Leaving usually costs the caller their inherited visibility of the poll, so a refetch
  // in place would only resolve to the 404 branch above; this navigates away deliberately, the way
  // CancelPoll does, and drops the list caches whose roster/tally figures just changed.
  const leave = useMutation({
    mutationFn: () => leavePoll(pollId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: pollKeys.listFamily('involved') })
      await queryClient.invalidateQueries({ queryKey: pollKeys.listFamily('discoverable') })
    },
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/polls" />
  }

  if (cancel.isSuccess || leave.isSuccess) {
    return <Navigate replace to="/polls" />
  }

  const data = detail.data
  const windowState = data ? pollWindowState(data) : null
  const isOrganizer = Boolean(data) && data?.organizerId === session?.userId
  const isPolling = windowState === 'polling'
  const deadlineText = data
    ? new Intl.DateTimeFormat(i18n.language, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(data.deadline))
    : ''

  function handleVote(optionId: string, choice: VoteChoiceValue) {
    vote.mutate({ optionId, choice })
  }

  function handleSuggest(event: React.FormEvent) {
    event.preventDefault()
    setSuggestError(null)
    if (!suggestValue) return
    const asInstant = new Date(suggestValue)
    if (Number.isNaN(asInstant.getTime())) return
    if ((data?.options.length ?? 0) >= MAX_OPTIONS) {
      setSuggestError(t('suggest.optionCap'))
      return
    }
    suggest.mutate(asInstant.toISOString())
  }

  const isTerminal =
    data?.status === 'finalized' || data?.status === 'expired' || data?.status === 'canceled'

  // D-186: the organizer holds the poll's own row and cannot leave it; guests are unaffected, so
  // this is gated on the viewer's own Active registered roster entry. A terminal poll refuses the
  // leave server-side, so the control goes away with the rest of the live surface.
  const canLeave =
    !isOrganizer &&
    !isTerminal &&
    (windowState === 'polling' || windowState === 'awaitingSelection') &&
    Boolean(
      session?.userId &&
        data?.roster.some((entry) => entry.userId === session.userId && entry.isActive),
    )

  const mutationError =
    vote.error ??
    (suggest.error && !suggestError ? suggest.error : null) ??
    guestPanelError ??
    finalizeError ??
    cancelError ??
    leave.error ??
    mute.error

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-poll-detail"
    >
      <h1
        ref={heading}
        aria-live="polite"
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {data?.title ?? t('common.loading', { ns: 'common' })}
      </h1>

      <p aria-live="polite" className="text-ink">
        {status}
      </p>

      {Boolean(detail.error || mutationError) && (
        <p role="alert">
          {(() => {
            const anyError = detail.error ?? mutationError
            return t(
              isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )
          })()}
        </p>
      )}

      {data && (
        <>
          <p className="flex flex-wrap items-center gap-2 text-ink">
            <PollStatusChip poll={data} />
          </p>

          {data.notes && <p className="text-ink">{data.notes}</p>}

          {windowState === 'polling' && (
            <p className="text-ink-soft">{t('detail.deadline', { deadline: deadlineText })}</p>
          )}

          {windowState === 'awaitingSelection' && (
            <p role="status">{t('detail.awaitingSelectionNotice')}</p>
          )}

          {windowState === 'expiredPending' && (
            <p role="status">{t('detail.expiredPendingNotice')}</p>
          )}

          {data.status === 'finalized' && (
            <p role="status">
              {data.createdSessionId ? (
                <>
                  {t('detail.finalizedNotice')}{' '}
                  <Link
                    className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    to={`/sessions/${data.createdSessionId}`}
                  >
                    {t('detail.viewSession')}
                  </Link>
                </>
              ) : (
                t('detail.finalizedNotice')
              )}
            </p>
          )}

          {data.status === 'expired' && <p role="status">{t('detail.expiredNotice')}</p>}
          {data.status === 'canceled' && <p role="status">{t('detail.canceledNotice')}</p>}

          <div>
            <Button
              aria-busy={mute.isPending}
              aria-pressed={data.muted}
              disabled={mute.isPending || isTerminal}
              type="button"
              variant={data.muted ? 'primary' : 'neutral'}
              onClick={() => mute.mutate()}
            >
              {data.muted ? t('mute.off') : t('mute.on')}
            </Button>
            <p className="mt-1 text-ink-soft">
              {isTerminal ? t('mute.disabledTerminal') : t('mute.copy')}
            </p>
          </div>

          <section aria-labelledby="poll-options-heading">
            <h2
              id="poll-options-heading"
              className="text-h2 font-display font-semibold text-ink"
            >
              {t('detail.options')}
            </h2>
            <VotingGrid
              disabled={!isPolling}
              locale={i18n.language}
              options={data.options}
              pendingOptionId={pendingOptionId}
              onVote={handleVote}
            />

            {isPolling && (
              <form
                className="mt-4 flex flex-wrap items-end gap-2"
                onSubmit={handleSuggest}
              >
                <div className="flex-1">
                  <label
                    className="block font-body font-semibold text-ink"
                    htmlFor="suggest-option-value"
                  >
                    {t('suggest.heading')}
                  </label>
                  <input
                    className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    id="suggest-option-value"
                    step={900}
                    type="datetime-local"
                    value={suggestValue}
                    onChange={(event) => setSuggestValue(event.target.value)}
                  />
                </div>
                <Button
                  aria-busy={suggest.isPending}
                  disabled={suggest.isPending}
                  type="submit"
                  variant="neutral"
                >
                  {t('suggest.add')}
                </Button>
              </form>
            )}
            {suggestNotice && (
              <p aria-live="polite" className="mt-1 text-ink-soft" role="status">
                {suggestNotice}
              </p>
            )}
            {suggestError && (
              <p role="alert">{suggestError}</p>
            )}
          </section>

          {canLeave && (
            <div>
              <Button
                aria-busy={leave.isPending}
                data-testid="poll-leave"
                disabled={leave.isPending}
                type="button"
                variant="neutral"
                onClick={() => leave.mutate()}
              >
                {t('leave.action')}
              </Button>
              <p className="mt-1 text-ink-soft">{t('leave.help')}</p>
            </div>
          )}

          <section aria-labelledby="poll-roster-heading">
            <h2
              id="poll-roster-heading"
              className="text-h2 font-display font-semibold text-ink"
            >
              {t('detail.roster')}
            </h2>
            <ul className="mt-2">
              {data.roster.map((entry) => (
                <li
                  key={entry.participationId}
                  className="flex items-center justify-between gap-2 py-1"
                  data-testid={`poll-roster-entry-${entry.participationId}`}
                >
                  <span className="flex min-w-0 items-center gap-2">
                    <AvatarDisc
                      emoji={entry.avatar?.emoji}
                      imgUrl={avatarImgUrl(entry.avatar)}
                      masked={entry.displayName === null}
                      seed={entry.userId ?? entry.guestIdentityId ?? entry.participationId}
                      size="xs"
                    />
                    <span className="truncate text-ink">
                      {entry.displayName ?? t('detail.anonymous')}
                    </span>
                  </span>
                  {!entry.isActive && (
                    <span className="text-ink-soft">{t('detail.removed')}</span>
                  )}
                </li>
              ))}
            </ul>
          </section>

          {isOrganizer && (windowState === 'awaitingSelection' || windowState === 'polling') && (
            <FinalizeSection
              early={windowState === 'polling'}
              locale={i18n.language}
              pollId={pollId}
              onError={setFinalizeError}
            />
          )}

          {isOrganizer && (windowState === 'polling' || windowState === 'awaitingSelection') && (
            <div>
              <CancelPollDialog pending={cancel.isPending} onConfirm={() => cancel.mutate()} />
            </div>
          )}

          {isOrganizer && (windowState === 'polling' || windowState === 'awaitingSelection') && (
            <GuestPanel
              canMint={windowState === 'polling'}
              clearError={() => setGuestPanelError(null)}
              pollId={pollId}
              roster={data.roster}
              onError={setGuestPanelError}
            />
          )}
        </>
      )}
    </main>
  )
}
