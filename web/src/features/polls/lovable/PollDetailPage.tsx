import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useLocation, useNavigate, useParams } from 'react-router'
import {
  Ban,
  CalendarClock,
  Check,
  Clock,
  History,
  Lock,
  Plus,
  Trash2,
  Trophy,
  Users,
} from 'lucide-react'
import { isApiError } from '../../../api/client'
import { useSession } from '../../../auth/useSession'
import { Button } from '../../../components/ui/button'
import {
  fieldControlClasses,
  fieldSelectClasses,
  neutralCtaClasses,
  raisedInteractiveClasses,
} from '../../../components/ui/classes'
import { Dialog } from '../../../components/ui/dialog'
import { BOTTOM_BAR_OFFSET_CLASSES } from '../../../components/shell/bottomBarOffset'
import { PageHeader } from '../../../components/shell/PageHeader'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { useToast } from '../../../components/ui/toastStore'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { ShareTag } from '../../groups/ShareTag'
import { getViewerProfile, viewerProfileKey } from '../../sessions/sessionQueries'
import { cn } from '../../availability/lovable/utils'
import {
  cancelPoll,
  createPollGuestLinks,
  deletePollOption,
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
  type GuestLinkInvitation,
  type PollDetail as PollDetailResponse,
} from '../pollQueries'
import { StatusPill, VoterAvatar, VoterRow } from './PollBits'
import { VoteGrid } from './VoteGrid'
import {
  mapFinalizationPreview,
  mapPollDetail,
  optionDayLabel,
  optionRange,
  platformLabel,
  rankedOptionIds,
  tallyFor,
  VOTE_META,
  formatCountdown,
  statusLabelKey,
  type FinalizationOption,
  type MappedPoll,
  type PollFormatters,
  type PollLabels,
  type PollOptionT,
  type Seat,
  type Voter,
  type VoteValue,
} from './pollData'

/**
 * Ported from the Lovable prototype's `src/routes/polls.$pollId.tsx` (784
 * lines, S4 of the verbatim port), byte-faithful to its section layout
 * (sticky summary sidebar · candidate list · sticky finalize bar · finalize/
 * suggest/delete dialogs) apart from:
 *  - No `AppHeader` (out of scope for this slice, matching S2/S3).
 *  - The prototype's mock in-memory `Poll`/local `setPoll` state is replaced
 *    end to end by `pollQueries.ts` + `./pollData`'s `mapPollDetail` (real
 *    `GetPollDetail`) and `mapFinalizationPreview` (real
 *    `GetPollFinalizationPreview`) — every mutation round-trips the server.
 *  - The prototype's per-vote-category avatar chips (`groups[].ids`) are
 *    restored using the server's `availableVoters`/`maybeVoters`/
 *    `unavailableVoters` lists (ordered by first-submission time), never a
 *    client-side re-tally of those lists into the displayed count (A-076 —
 *    `availableCount`/`maybeCount`/`unavailableCount` alone remain
 *    authoritative for the number shown next to each icon).
 *  - The prototype's "Invited" sidebar note (`Organizer` / `Voted` / `No
 *    reply yet`) drops the per-voter "voted" fact for the same reason — no
 *    per-voter vote list exists on the wire. It keeps `Organizer` and
 *    `Removed` (D-044/roster `isActive`), both real roster facts.
 *  - The prototype's footer "Other polls" cross-links (reading its own mock
 *    `POLLS` fixture) has no real equivalent at this scope and is dropped.
 *  - The prototype's `SuggestDialog` day/time-of-week picker assumed a fixed
 *    demo week; the real `SuggestPollOption` op takes an absolute instant,
 *    so this arm uses a real `datetime-local` field (same control the S5
 *    page shipped) inside the prototype's dialog chrome.
 *  - New real capability the prototype only faked client-side: option
 *    deletion now calls the real `DeletePollOption` endpoint (present on
 *    the API since S0 but never wired to any page) directly through the
 *    generated `client`, gated to the organizer while the poll is Polling.
 *  - Guest-link minting/copy/revoke and claimed-guest removal (D-044), mute/
 *    unmute (D-090/D-113), leave (D-186), cancel (D-156) and the mid-flight
 *    invite entry point (D-193) have no home in the prototype's structure at
 *    all (it never modeled guests, muting, leaving or cancelling) — each is
 *    placed as its own `surface` card in the sidebar, in the same relative
 *    order the pre-port `PollDetailPage` used (guests -> notifications ->
 *    danger zone).
 *  - Every user-facing string moves through `useTranslation('polls')`
 *    (existing S5/S6/S7 keys reused where the meaning matches exactly; new
 *    keys live under `polls:detailLovable`).
 *  - Countdown math (`deadlineLeft`/`pickLeft`) reads the RAW
 *    `PollDetailResponse.deadline`/`selectionWindowEndsAt` wire instants
 *    against `Date.now()` directly — never the fake-local `MappedPoll`
 *    dates, which are shifted by the viewer's zone offset and would corrupt
 *    a duration (D-046: fake-local is a *display* boundary, not a clock).
 */

const MAX_OPTIONS = 12

/**
 * D-211: with no grid on this page, suggesting a time collects its length from
 * a plain select. The choices mirror the lengths the create grid paints most,
 * all on D-131's 15-minute grid.
 */
const SUGGEST_DURATION_CHOICES = [30, 60, 90, 120, 180, 240] as const
const DEFAULT_SUGGEST_MINUTES = 60
/** Matches the prototype's `g.ids.slice(0, 5)` collapse count for per-option voter chips. */
const CHIP_LIMIT = 5

function useNowTicker() {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60_000)
    return () => clearInterval(timer)
  }, [])
  return now
}

/* ------------------------------------------------------------------ */

function VoteRadioGroup({
  option,
  disabled,
  pending,
  legend,
  onVote,
}: {
  option: PollOptionT
  disabled: boolean
  pending: boolean
  legend: string
  onVote: (choice: VoteValue) => void
}) {
  const { t } = useTranslation('polls')
  const choices: VoteValue[] = ['available', 'maybe', 'unavailable']

  function onKeyDown(event: React.KeyboardEvent<HTMLButtonElement>, current: VoteValue) {
    const delta =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0
    if (delta === 0) return
    event.preventDefault()
    const from = choices.indexOf(current)
    const next = choices[(from + delta + choices.length) % choices.length]
    if (!disabled) onVote(next)
    const radios =
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role=radio]')
    radios?.[choices.indexOf(next)]?.focus()
  }

  return (
    <div className="mt-3">
      <p className="mb-1.5 text-[12px] font-bold text-muted-foreground">
        {t('detailLovable.yourVoteHeading')}
      </p>
      <div
        aria-busy={pending}
        aria-label={legend}
        className="grid grid-cols-3 gap-1.5"
        data-testid={`vote-radiogroup-${option.id}`}
        role="radiogroup"
      >
        {choices.map((choice) => {
          const meta = VOTE_META[choice]
          const Icon = meta.icon
          const active = option.viewerVote === choice
          return (
            <button
              key={choice}
              aria-checked={active}
              aria-label={t('vote.controlLabel', { time: legend, value: t(`vote.${choice}`) })}
              className={cn(
                'flex min-h-11 min-w-11 items-center justify-center gap-1.5 rounded-xl border font-display text-[13px] font-bold transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:opacity-50',
                active
                  ? meta.cls
                  : 'border-border bg-background text-muted-foreground hover:border-foreground/30',
              )}
              data-testid={`vote-${choice}-${option.id}`}
              disabled={disabled}
              role="radio"
              tabIndex={active || (!option.viewerVote && choice === 'available') ? 0 : -1}
              type="button"
              onClick={() => !disabled && onVote(choice)}
              onKeyDown={(event) => onKeyDown(event, option.viewerVote ?? 'available')}
            >
              {active ? <Check className="size-4" /> : <Icon className="size-4" />}
              <span className="truncate">{t(`vote.${choice}`)}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
}

function OptionCard({
  poll,
  option,
  rank,
  isLeader,
  isPast,
  canVote,
  isOrganizer,
  formatters,
  pendingOptionId,
  previewPending,
  onVote,
  onPreview,
  onDelete,
}: {
  poll: MappedPoll
  option: PollOptionT
  rank: number
  isLeader: boolean
  isPast: boolean
  canVote: boolean
  isOrganizer: boolean
  formatters: PollFormatters
  pendingOptionId: string | null
  previewPending: boolean
  onVote: (choice: VoteValue) => void
  onPreview: () => void
  onDelete: () => void
}) {
  const { t } = useTranslation('polls')
  const tally = tallyFor(option, poll.roster.filter((voter) => voter.isActive).length)
  const total = poll.roster.filter((voter) => voter.isActive).length
  const isFinal = poll.selectedOptionId === option.id
  const muted = Boolean(poll.selectedOptionId) && !isFinal
  const timeText = `${optionDayLabel(option, formatters)} · ${optionRange(option, formatters)}`
  const legend = t('vote.legend', { time: timeText })

  const groups: { key: VoteValue | 'pending'; count: number; voters: Voter[] }[] = [
    { key: 'available', count: tally.available, voters: option.availableVoters },
    { key: 'maybe', count: tally.maybe, voters: option.maybeVoters },
    { key: 'unavailable', count: tally.unavailable, voters: option.unavailableVoters },
    { key: 'pending', count: tally.pending, voters: [] },
  ]

  return (
    <article
      className={cn(
        'surface p-4 transition-opacity',
        isFinal && 'border-2 border-primary',
        muted && 'opacity-60',
      )}
      data-testid={`poll-option-${option.id}`}
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="flex min-w-0 items-start gap-3">
          <span
            aria-label={t('detailLovable.rank', { rank })}
            className={cn(
              'grid size-11 shrink-0 place-items-center rounded-xl border font-display text-[15px] font-extrabold tabular-nums',
              isLeader && !poll.selectedOptionId
                ? 'border-primary bg-primary text-primary-foreground'
                : 'border-border bg-muted text-muted-foreground',
            )}
          >
            #{rank}
          </span>
          <div className="min-w-0">
            <h3 className="font-display text-base font-extrabold tabular-nums">{timeText}</h3>
            <p className="flex flex-wrap items-center gap-1.5 text-[12px] text-muted-foreground">
              {isLeader && !poll.selectedOptionId && (
                <span className="inline-flex items-center gap-1 rounded-full border border-primary bg-primary/15 px-2 py-0.5 font-bold text-foreground">
                  <Trophy className="size-3" /> {t('detailLovable.currentLeader')}
                </span>
              )}
              {isFinal && (
                <span className="inline-flex items-center gap-1 rounded-full border border-foreground/30 bg-accent px-2 py-0.5 font-bold text-accent-foreground">
                  <Lock className="size-3" /> {t('detailLovable.winningTime')}
                </span>
              )}
              {isPast && (
                <span className="inline-flex items-center gap-1 rounded-full border border-border bg-muted px-2 py-0.5 font-bold">
                  <History className="size-3" /> {t('detailLovable.alreadyPassed')}
                </span>
              )}
              <span className="tabular-nums" data-testid={`poll-option-tally-${option.id}`}>
                {t('detailLovable.repliedCount', {
                  responded: tally.available + tally.maybe + tally.unavailable,
                  total,
                })}
              </span>
            </p>
            {option.suggestedBy && (
              <p
                className="text-[12px] text-muted-foreground"
                data-testid={`poll-option-suggested-by-${option.id}`}
              >
                {t('detailLovable.suggestedBy', { name: option.suggestedBy.name })}
              </p>
            )}
          </div>
        </div>

        {isOrganizer && !poll.selectedOptionId && (
          <div className="flex items-center gap-1.5">
            <Button
              aria-busy={previewPending}
              className="h-11 rounded-xl text-[13px] font-bold"
              data-testid={`finalize-option-${option.id}`}
              disabled={previewPending}
              type="button"
              variant="neutral"
              onClick={onPreview}
            >
              <Users className="size-4" /> {t('detailLovable.previewSeats')}
            </Button>
            <Button
              aria-label={t('detailLovable.deleteOption')}
              className="size-11 rounded-xl text-danger"
              type="button"
              variant="neutral"
              onClick={onDelete}
            >
              <Trash2 className="size-4" />
            </Button>
          </div>
        )}
      </div>

      <div className="mt-3 flex h-3 overflow-hidden rounded-full border border-border bg-muted">
        {(['available', 'maybe', 'unavailable'] as VoteValue[]).map((key) => (
          <span
            key={key}
            className={cn(
              'h-full border-r border-card last:border-r-0',
              key === 'available' && 'bg-free',
              key === 'maybe' && 'bg-maybe',
              key === 'unavailable' && 'bg-busy',
            )}
            style={{ width: total > 0 ? `${(tally[key] / total) * 100}%` : '0%' }}
          />
        ))}
      </div>

      <ul className="mt-2 grid gap-1.5 sm:grid-cols-2">
        {groups.map((group) => {
          const meta = group.key === 'pending' ? null : VOTE_META[group.key]
          const Icon = meta?.icon ?? Clock
          const label = group.key === 'pending' ? t('detail.tallyNone') : t(`vote.${group.key}`)
          const shown = group.voters.slice(0, CHIP_LIMIT)
          const overflow = group.voters.length - shown.length
          return (
            <li
              key={group.key}
              className="flex min-h-9 items-center gap-2 rounded-xl border border-border bg-background px-2 py-1.5"
            >
              <span
                className={cn(
                  'grid size-6 shrink-0 place-items-center rounded-lg border',
                  meta?.cls ?? 'border-border bg-muted text-muted-foreground',
                )}
              >
                <Icon className="size-3.5" />
              </span>
              <span className="shrink-0 text-[12px] font-bold tabular-nums">
                {label} {group.count}
              </span>
              {shown.length > 0 && (
                <span
                  aria-label={t('detailLovable.voterChipsLabel', {
                    category: label,
                    names: shown.map((voter) => voter.name).join(', '),
                  })}
                  className="ml-auto flex min-w-0 items-center -space-x-1.5"
                  data-testid={`poll-option-voters-${option.id}-${group.key}`}
                >
                  {shown.map((voter) => (
                    <VoterAvatar key={voter.id} ring="border border-card" voter={voter} />
                  ))}
                  {overflow > 0 && (
                    <span
                      aria-label={t('detailLovable.voterChipsOverflow', { count: overflow })}
                      className="grid h-[19px] min-w-[19px] place-items-center rounded-full border border-border bg-muted px-1 text-[9px] font-bold text-muted-foreground"
                    >
                      +{overflow}
                    </span>
                  )}
                </span>
              )}
            </li>
          )
        })}
      </ul>

      <VoteRadioGroup
        disabled={!canVote}
        legend={legend}
        option={option}
        pending={pendingOptionId === option.id}
        onVote={onVote}
      />
    </article>
  )
}

/* ------------------------------------------------------------------ */

function FinalizeDialog({
  poll,
  option,
  optionCard,
  formatters,
  early,
  pending,
  onClose,
  onConfirm,
}: {
  poll: MappedPoll
  option: FinalizationOption | null
  optionCard: PollOptionT | null
  formatters: PollFormatters
  early: boolean
  pending: boolean
  onClose: () => void
  onConfirm: (optionId: string) => void
}) {
  const { t } = useTranslation('polls')
  const cancelButton = useRef<HTMLButtonElement>(null)
  const seated = option?.seats.filter((seat) => seat.seated) ?? []
  const waitlisted = option?.seats.filter((seat) => !seat.seated) ?? []
  const short = option ? seated.length < poll.minAttendance : false
  const notViableYet = early && option ? !option.meetsMinimum : false
  const optionText = optionCard
    ? `${optionDayLabel(optionCard, formatters)} ${optionRange(optionCard, formatters)}`
    : null

  return (
    <Dialog.Root open={Boolean(option)} onOpenChange={(open) => !open && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-finalize-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 max-h-[85vh] w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t(early ? 'finalize.confirmTitleEarly' : 'finalize.confirmTitle')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="poll-finalize-description">
            {optionText &&
              t(early ? 'finalize.confirmBodyEarly' : 'finalize.confirmBody', { time: optionText })}
          </Dialog.Description>

          {early && (
            <p className="mt-2 text-ink" data-testid="finalize-early-notice">
              {t('finalize.earlyNotice')}
            </p>
          )}
          <p className="mt-1 text-ink-soft">{t('finalize.previewGuarantee')}</p>

          {notViableYet ? (
            <p className="mt-3 text-danger" role="alert">
              {t('finalize.belowMinimum')}
            </p>
          ) : (
            <div className="mt-3 space-y-3" data-testid="finalize-seats">
              <div>
                <p className="flex items-center gap-1.5 font-display text-[13px] font-extrabold">
                  <Check className="size-4" /> {t('detailLovable.seated')} · {seated.length}
                  {poll.maxCapacity ? `/${poll.maxCapacity}` : ''}
                </p>
                <ul className="mt-1.5 space-y-1">
                  {seated.map((seat, index) => (
                    <SeatRow key={seat.voterId} index={index + 1} seat={seat} variant="seated" />
                  ))}
                  {seated.length === 0 && (
                    <li className="text-[12px] text-muted-foreground">
                      {t('detailLovable.nobodyCanMake')}
                    </li>
                  )}
                </ul>
              </div>
              <div>
                <p className="flex items-center gap-1.5 font-display text-[13px] font-extrabold">
                  <Clock className="size-4" /> {t('detailLovable.waitlist')} · {waitlisted.length}
                </p>
                <ul className="mt-1.5 space-y-1">
                  {waitlisted.map((seat, index) => (
                    <SeatRow
                      key={seat.voterId}
                      index={index + 1}
                      prefix="W"
                      seat={seat}
                      variant="waitlist"
                    />
                  ))}
                  {waitlisted.length === 0 && (
                    <li className="text-[12px] text-muted-foreground">
                      {t('detailLovable.everyoneFits')}
                    </li>
                  )}
                </ul>
              </div>
              {short && (
                <p className="rounded-xl border border-danger/40 bg-danger/10 px-2.5 py-2 text-[12px] font-semibold text-danger">
                  {t('detailLovable.belowMinimumNotice', { min: poll.minAttendance })}
                </p>
              )}
            </div>
          )}

          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button
              ref={cancelButton}
              className={neutralCtaClasses}
              type="button"
              onClick={onClose}
            >
              {t('detailLovable.notYet')}
            </button>
            <Button
              aria-busy={pending}
              data-testid="finalize-confirm"
              disabled={pending || notViableYet}
              type="button"
              variant="primary"
              onClick={() => option && onConfirm(option.optionId)}
            >
              <Trophy className="size-4" /> {t('detailLovable.lockInTime')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

function SeatRow({
  seat,
  index,
  prefix,
  variant,
}: {
  seat: Seat
  index: number
  prefix?: string
  variant: 'seated' | 'waitlist'
}) {
  return (
    <li
      className={cn(
        'flex items-center gap-2 rounded-xl border px-2.5 py-2',
        variant === 'seated' ? 'border-free/60 bg-free text-free-foreground' : 'border-border bg-muted',
      )}
    >
      <span className="w-6 shrink-0 text-[12px] font-bold tabular-nums">
        {prefix ?? ''}
        {index}
      </span>
      <span aria-hidden="true">
        <AvatarDisc emoji={seat.emoji} imgUrl={seat.imgUrl} masked={seat.masked} seed={seat.seed} size="xs" />
      </span>
      <span className="min-w-0 truncate text-[13px] font-semibold">{seat.name}</span>
    </li>
  )
}

/* ------------------------------------------------------------------ */

function SuggestDialog({
  error,
  open,
  pending,
  onClose,
  onAdd,
}: {
  error: string | null
  open: boolean
  pending: boolean
  onClose: () => void
  onAdd: (startTimeLocal: string, durationMinutes: number) => void
}) {
  const { t } = useTranslation('polls')
  const cancelButton = useRef<HTMLButtonElement>(null)
  const [value, setValue] = useState('')
  const [duration, setDuration] = useState(String(DEFAULT_SUGGEST_MINUTES))

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-suggest-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('detailLovable.suggestDialogTitle')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="poll-suggest-description">
            {t('detailLovable.suggestDialogHelp')}
          </Dialog.Description>

          <div className="mt-4 flex flex-col gap-1.5">
            <label className="font-body font-semibold text-ink" htmlFor="suggest-option-value">
              {t('suggest.heading')}
            </label>
            <input
              className={fieldControlClasses}
              id="suggest-option-value"
              step={900}
              type="datetime-local"
              value={value}
              onChange={(event) => setValue(event.target.value)}
            />
          </div>

          {/* D-211: an option owns its own length, so suggesting one has to
              collect it — this is the no-grid equivalent of painting a band. */}
          <div className="mt-3 flex flex-col gap-1.5">
            <label className="font-body font-semibold text-ink" htmlFor="suggest-option-duration">
              {t('suggest.duration')}
            </label>
            <select
              className={fieldSelectClasses}
              id="suggest-option-duration"
              value={duration}
              onChange={(event) => setDuration(event.target.value)}
            >
              {SUGGEST_DURATION_CHOICES.map((minutes) => (
                <option key={minutes} value={minutes}>
                  {t('suggest.durationOption', { minutes })}
                </option>
              ))}
            </select>
          </div>

          <p className="mt-4 flex items-center gap-2 rounded-xl border border-border bg-muted/50 px-2.5 py-2 text-[12px] text-muted-foreground">
            <CalendarClock className="size-4 shrink-0" />
            {t('detailLovable.nudgeNotice')}
          </p>

          {error && (
            <p className="mt-3" role="alert">
              {error}
            </p>
          )}

          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button
              ref={cancelButton}
              className={neutralCtaClasses}
              type="button"
              onClick={onClose}
            >
              {t('detailLovable.cancel')}
            </button>
            <Button
              aria-busy={pending}
              disabled={pending || !value}
              type="button"
              variant="primary"
              onClick={() => {
                onAdd(value, Number(duration))
                setValue('')
              }}
            >
              <Plus className="size-4" /> {t('suggest.add')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/* ------------------------------------------------------------------ */

function DeleteOptionDialog({
  open,
  pending,
  onClose,
  onConfirm,
}: {
  open: boolean
  pending: boolean
  onClose: () => void
  onConfirm: () => void
}) {
  const { t } = useTranslation('polls')
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-delete-option-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('detailLovable.deleteOptionTitle')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="poll-delete-option-description">
            {t('detailLovable.deleteOptionBody')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button
              ref={cancelButton}
              className={neutralCtaClasses}
              type="button"
              onClick={onClose}
            >
              {t('detailLovable.keepIt')}
            </button>
            <Button
              aria-busy={pending}
              disabled={pending}
              type="button"
              variant="danger"
              onClick={onConfirm}
            >
              <Trash2 className="size-4" /> {t('detailLovable.deleteOption')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/* ------------------------------------------------------------------ */

function CancelPollDialog({ pending, onConfirm }: { pending: boolean; onConfirm: () => void }) {
  const { t } = useTranslation('polls')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button disabled={pending} type="button" variant="danger">
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
          <Dialog.Description className="mt-2 text-ink-soft" id="poll-cancel-description">
            {t('cancel.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
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

function RemoveGuestDialog({
  displayName,
  pending,
  onConfirm,
}: {
  displayName: string
  pending: boolean
  onConfirm: () => void
}) {
  const { t } = useTranslation('polls')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-label={`${t('guests.remove')} ${displayName}`}
          disabled={pending}
          type="button"
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
          <Dialog.Description className="mt-2 text-ink-soft" id="poll-remove-guest-description">
            {t('guests.removeConfirm')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
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

/**
 * design §5/§6.1 guest-management panel (D-044) — placed as its own sidebar
 * card since the prototype never modeled guests at all. Same mint/copy/
 * revoke/remove idiom as the pre-port `PollDetailPage`'s `GuestPanel`.
 */
function GuestLinksPanel({
  pollId,
  roster,
  canMint,
  onError,
  clearError,
  initialLinks,
}: {
  pollId: string
  roster: PollDetailResponse['roster']
  canMint: boolean
  onError: (error: unknown) => void
  clearError: () => void
  initialLinks?: GuestLinkInvitation[]
}) {
  const { t } = useTranslation('polls')
  const queryClient = useQueryClient()
  const toast = useToast()
  const [linkCount, setLinkCount] = useState('1')
  const [guestLinks, setGuestLinks] = useState<GuestLinkInvitation[]>(
    () => initialLinks ?? [],
  )
  // One-shot: `initialLinks` only ever arrives as router state from the create
  // flow's own navigation, so this banner is true for exactly the render(s)
  // right after minting and never again after a refresh (router state does
  // not survive one).
  const [showJustCreatedHint] = useState(() => Boolean(initialLinks?.length))
  const [announcement, setAnnouncement] = useState('')

  const mint = useMutation({
    mutationFn: () => createPollGuestLinks(pollId, Number(linkCount)),
    onSuccess: (created) => {
      clearError()
      setGuestLinks((current) => [...current, ...created])
    },
    onError,
  })

  async function copy(url: string) {
    const label = t('guests.linkLabel')
    try {
      await navigator.clipboard.writeText(url)
      setAnnouncement(t('live.copied', { label }))
      toast({ message: t('live.copied', { label }) })
    } catch (caught) {
      onError(caught)
    }
  }

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

  const claimedGuests = roster.filter((entry) => entry.guestIdentityId && entry.isActive)

  return (
    <section className="order-4 surface p-4">
      <h3 className="font-display text-base font-extrabold">{t('guests.heading')}</h3>

      {canMint && (
        <div className="mt-3 flex flex-wrap items-end gap-2">
          <div className="flex flex-col gap-1.5">
            <label className="text-[12px] font-bold text-muted-foreground" htmlFor="poll-guest-link-count">
              {t('create.guestLinks')}
            </label>
            <input
              className={`${fieldControlClasses} w-24`}
              id="poll-guest-link-count"
              max={20}
              min={1}
              type="number"
              value={linkCount}
              onChange={(event) => setLinkCount(event.target.value)}
            />
          </div>
          <Button
            aria-busy={mint.isPending}
            className="h-11 rounded-xl"
            disabled={mint.isPending}
            type="button"
            variant="neutral"
            onClick={() => mint.mutate()}
          >
            {t('guests.mint')}
          </Button>
        </div>
      )}

      {showJustCreatedHint && (
        <StatusBanner className="mt-3" role="status" variant="success">
          {t('guests.justCreatedHint')}
        </StatusBanner>
      )}

      {guestLinks.length > 0 && (
        <ul className="mt-2 space-y-1.5">
          {guestLinks.map((link) => (
            <li
              key={link.guestLinkId}
              className="grid grid-cols-[1fr_auto] items-center gap-2 rounded-xl border border-border bg-background px-2.5 py-2"
            >
              <span className="min-w-0 truncate font-mono text-[12px] text-ink">{link.url}</span>
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

      <p aria-live="polite" className="sr-only">
        {announcement}
      </p>

      {claimedGuests.length > 0 && (
        <ul className="mt-2 space-y-1.5">
          {claimedGuests.map((entry) => (
            <li
              key={entry.participationId}
              className="flex items-center justify-between gap-2 rounded-xl border border-border bg-background px-2.5 py-2"
            >
              <span className="min-w-0 truncate text-[13px] font-semibold">
                {entry.displayName ?? t('detail.anonymous')}
              </span>
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

/* ------------------------------------------------------------------ */

export function PollDetailPage() {
  const { t, i18n } = useTranslation(['polls', 'common'])
  const { pollId = '' } = useParams()
  const { data: session } = useSession()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const location = useLocation()
  const now = useNowTicker()

  // One-shot handoff from `PollCreatePage`'s create mutation (router state,
  // not a query) — the only place the minted guest links are ever returned.
  // react-router persists `history.state.usr` across refresh/session-restore
  // (it is not one-shot on its own), so once the tokens are captured into
  // component state we replace the history entry with null state to keep
  // them out of browser history going forward.
  const [mintedGuestLinks] = useState(
    () => (location.state as { mintedGuestLinks?: GuestLinkInvitation[] } | null)
      ?.mintedGuestLinks,
  )

  useEffect(() => {
    if (location.state) {
      navigate('.', { replace: true, state: null })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const [announcement, setAnnouncement] = useState('')
  const [pendingOptionId, setPendingOptionId] = useState<string | null>(null)
  const [previewOptionId, setPreviewOptionId] = useState<string | null>(null)
  const [suggestOpen, setSuggestOpen] = useState(false)
  const [deleteOptionId, setDeleteOptionId] = useState<string | null>(null)
  const [suggestError, setSuggestError] = useState<string | null>(null)
  const [guestPanelError, setGuestPanelError] = useState<unknown>(null)
  const [otherError, setOtherError] = useState<unknown>(null)

  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const timeZone = profile.data?.timeZone ?? 'UTC'

  const detail = useQuery({
    queryKey: pollKeys.detail(pollId),
    queryFn: () => getPollDetail(pollId),
    enabled: Boolean(pollId),
    retry: false,
  })

  const labels: PollLabels = { anonymousVoter: t('detail.anonymous') }
  const poll = useMemo(
    () => mapPollDetail(detail.data, timeZone, labels),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [detail.data, timeZone, i18n.language],
  )

  const windowState = detail.data ? pollWindowState(detail.data) : null
  const isOrganizer = Boolean(poll) && poll?.organizerId === session?.userId
  const isPolling = windowState === 'polling'
  const isAwaitingSelection = windowState === 'awaitingSelection'
  const isTerminal = poll
    ? poll.status === 'finalized' || poll.status === 'expired' || poll.status === 'canceled'
    : false

  const formatters: PollFormatters = useMemo(
    () => ({
      day: new Intl.DateTimeFormat(i18n.language, {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
        timeZone: 'UTC',
      }),
      time: new Intl.DateTimeFormat(i18n.language, {
        hour: 'numeric',
        minute: '2-digit',
        timeZone: 'UTC',
      }),
    }),
    [i18n.language],
  )

  const rawOptionsById = useMemo(
    () => new Map((detail.data?.options ?? []).map((option) => [option.id, option])),
    [detail.data],
  )
  const rawRosterById = useMemo(
    () => new Map((detail.data?.roster ?? []).map((entry) => [entry.participationId, entry])),
    [detail.data],
  )

  const ranked = poll ? rankedOptionIds(poll.options) : []
  const leaderId = ranked[0]

  // D-046: countdowns read the raw wire instants, never the fake-local
  // `MappedPoll` dates (those are shifted by the viewer's zone offset). `now`
  // ticks every minute purely to force this to re-render — it is otherwise
  // interchangeable with `Date.now()` here.
  const deadlineLeftMin = detail.data ? (new Date(detail.data.deadline).getTime() - now) / 60_000 : 0
  const pickLeftMin = detail.data
    ? (new Date(detail.data.selectionWindowEndsAt).getTime() - now) / 60_000
    : 0

  async function afterVote(message: string) {
    setAnnouncement(message)
    setPendingOptionId(null)
    await queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    await queryClient.invalidateQueries({ queryKey: pollKeys.finalizationPreview(pollId) })
  }

  const vote = useMutation({
    mutationFn: ({ optionId, choice }: { optionId: string; choice: VoteValue }) => {
      setPendingOptionId(optionId)
      return voteOnPollOption(pollId, optionId, choice)
    },
    onSuccess: () => afterVote(t('vote.updated')),
    onError: () => setPendingOptionId(null),
  })

  const suggest = useMutation({
    mutationFn: ({ startTime, durationMinutes }: { startTime: string; durationMinutes: number }) =>
      suggestPollOption(pollId, startTime, durationMinutes),
    onSuccess: () => {
      setSuggestOpen(false)
      setAnnouncement(t('suggest.added'))
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'poll_option_cap') {
        setSuggestError(t('suggest.optionCap'))
        return
      }
      setSuggestError(
        t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
          defaultValue: t('errors.generic'),
        }),
      )
    },
  })

  const deleteOption = useMutation({
    mutationFn: (optionId: string) => deletePollOption(pollId, optionId),
    onSuccess: () => {
      setDeleteOptionId(null)
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
    onError: (error) => {
      setDeleteOptionId(null)
      setOtherError(error)
    },
  })

  const mute = useMutation({
    mutationFn: async () => {
      const wasMuted = poll?.muted ?? false
      if (wasMuted) {
        await unmutePoll(pollId)
      } else {
        await mutePoll(pollId)
      }
      return !wasMuted
    },
    onSuccess: (nowMuted) => {
      setAnnouncement(nowMuted ? t('mute.mutedAnnouncement') : t('mute.unmutedAnnouncement'))
      return queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
    },
  })

  const cancel = useMutation({
    mutationFn: () => cancelPoll(pollId),
    onError: setOtherError,
  })

  const leave = useMutation({
    mutationFn: () => leavePoll(pollId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: pollKeys.listFamily('involved') })
      await queryClient.invalidateQueries({ queryKey: pollKeys.listFamily('discoverable') })
    },
  })

  const preview = useQuery({
    queryKey: pollKeys.finalizationPreview(pollId),
    queryFn: () => getPollFinalizationPreview(pollId),
    enabled: isOrganizer && (isPolling || isAwaitingSelection),
    retry: false,
  })

  const mappedPreviewOptions = mapFinalizationPreview(preview.data, labels)
  const viableEarly = mappedPreviewOptions.filter((option) => !isPolling || option.meetsMinimum)

  const finalize = useMutation({
    mutationFn: (optionId: string) => finalizePoll(pollId, optionId),
    onSuccess: (response) => navigate(`/sessions/${response.sessionId}`),
    onError: setOtherError,
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/polls" />
  }
  if (cancel.isSuccess || leave.isSuccess) {
    return <Navigate replace to="/polls" />
  }

  function handleVote(optionId: string, choice: VoteValue) {
    vote.mutate({ optionId, choice })
  }

  function handleSuggest(startTimeLocal: string, durationMinutes: number) {
    setSuggestError(null)
    const asInstant = new Date(startTimeLocal)
    if (Number.isNaN(asInstant.getTime())) return
    if ((poll?.options.length ?? 0) >= MAX_OPTIONS) {
      setSuggestError(t('suggest.optionCap'))
      return
    }
    suggest.mutate({ startTime: asInstant.toISOString(), durationMinutes })
  }

  const canLeave =
    !isOrganizer &&
    !isTerminal &&
    (windowState === 'polling' || windowState === 'awaitingSelection') &&
    Boolean(
      session?.userId &&
        detail.data?.roster.some((entry) => entry.userId === session.userId && entry.isActive),
    )

  const mutationError =
    vote.error ??
    (suggest.error && !suggestError ? suggest.error : null) ??
    guestPanelError ??
    otherError ??
    leave.error ??
    mute.error ??
    preview.error

  const previewOption = previewOptionId
    ? (mappedPreviewOptions.find((option) => option.optionId === previewOptionId) ?? null)
    : null
  const previewOptionCard = previewOptionId
    ? (poll?.options.find((option) => option.id === previewOptionId) ?? null)
    : null

  const deleteOptionCard = deleteOptionId
    ? (poll?.options.find((option) => option.id === deleteOptionId) ?? null)
    : null

  const finalOptionCard = poll?.options.find((option) => option.id === poll.selectedOptionId)

  return (
    <div className="min-h-screen bg-background pb-32" data-testid="screen-poll-detail">
      <p aria-live="polite" className="sr-only" role="status">
        {announcement}
      </p>

      {detail.isPending && (
        <p className="p-6 text-ink-soft" role="status">
          {t('common.loading', { ns: 'common' })}
        </p>
      )}

      {Boolean(detail.error || mutationError) && (
        <div className="mx-auto max-w-[1400px] px-3 pt-4 sm:px-6">
          <StatusBanner role="alert" variant="danger">
            {(() => {
              const anyError = detail.error ?? mutationError
              return t(isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic', {
                defaultValue: t('errors.generic'),
              })
            })()}
          </StatusBanner>
        </div>
      )}

      {poll && (
        <>
          <div className="mx-auto max-w-[1400px] px-3 sm:px-6">
            <PageHeader
              actions={
                <div className="flex flex-wrap items-center gap-2">
                  <StatusPill label={t(statusLabelKey(poll.status))} status={poll.status} />
                  <ShareTag share={poll} />
                </div>
              }
              back={{ label: t('nav.backToList'), to: '/polls' }}
              description={`${poll.game ? `${poll.game.name} · ` : ''}${platformLabel(poll.platform, t)}`}
              title={poll.title}
            />
          </div>
          <main className="mx-auto grid max-w-[1400px] gap-4 px-3 pb-4 sm:px-6 lg:grid-cols-[minmax(0,1fr)_340px]">
          <div className="contents lg:order-2 lg:block lg:min-w-0 lg:space-y-4 lg:sticky lg:top-4 lg:self-start">
            <section className="order-1 surface p-4" data-testid="poll-summary-card">
              <dl className="grid grid-cols-2 gap-2 text-[12px]">
                <Meta
                  label={t('detailLovable.meta.privacy')}
                  value={
                    poll.visibility === 'private'
                      ? t('detailLovable.privacyPrivate')
                      : poll.visibility === 'groups'
                        ? t('detailLovable.privacyGroups')
                        : t('detailLovable.privacyPublic')
                  }
                />
                <Meta label={t('detailLovable.meta.minimum')} value={String(poll.minAttendance)} />
                <Meta
                  label={t('detailLovable.meta.capacity')}
                  value={poll.maxCapacity ? String(poll.maxCapacity) : t('detailLovable.meta.unlimited')}
                />
              </dl>

              {poll.notes && (
                <p className="mt-3 rounded-xl border border-border bg-muted/50 p-2.5 text-[12px]">
                  {poll.notes}
                </p>
              )}

              <div className="mt-3 flex items-center gap-2 rounded-xl border border-border bg-background px-3 py-2.5">
                {isPolling ? (
                  <>
                    <Clock className="size-4 shrink-0" />
                    <span className="text-[13px] font-semibold">
                      {t('detailLovable.countdown.votingClosesIn', {
                        time: formatCountdown(deadlineLeftMin),
                      })}
                    </span>
                  </>
                ) : isAwaitingSelection ? (
                  <>
                    <Clock className="size-4 shrink-0" />
                    <span className="text-[13px] font-semibold">
                      {t('detailLovable.countdown.pickWindow', {
                        time: formatCountdown(pickLeftMin),
                      })}
                    </span>
                  </>
                ) : poll.status === 'finalized' && finalOptionCard ? (
                  <>
                    <Trophy className="size-4 shrink-0" />
                    <span className="text-[13px] font-semibold">
                      {t('detailLovable.countdown.lockedIn', {
                        time: `${optionDayLabel(finalOptionCard, formatters)} ${optionRange(finalOptionCard, formatters)}`,
                      })}
                    </span>
                  </>
                ) : poll.status === 'expired' || windowState === 'expiredPending' ? (
                  <>
                    <History className="size-4 shrink-0" />
                    <span className="text-[13px] font-semibold">
                      {t('detailLovable.countdown.expiredBanner')}
                    </span>
                  </>
                ) : (
                  <>
                    <Ban className="size-4 shrink-0" />
                    <span className="text-[13px] font-semibold">
                      {t('detailLovable.countdown.canceledBanner')}
                    </span>
                  </>
                )}
              </div>

              {poll.status === 'finalized' && detail.data?.createdSessionId && (
                <p className="mt-3">
                  <Link
                    className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    to={`/sessions/${detail.data.createdSessionId}`}
                  >
                    {t('detail.viewSession')}
                  </Link>
                </p>
              )}

              {isOrganizer && isPolling && (
                <Link
                  className={`mt-3 inline-flex items-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                  to={`/polls/${pollId}/invite`}
                >
                  {t('invite.heading')}
                </Link>
              )}
            </section>

            <section className="order-3 surface p-4">
              <h3 className="flex items-center gap-2 font-display text-base font-extrabold">
                <Users className="size-4" /> {t('detailLovable.invited', { count: poll.roster.length })}
              </h3>
              <ul className="mt-2 space-y-1.5">
                {[...rawRosterById.values()].map((entry) => {
                  const voter = poll.roster.find((candidate) => candidate.id === entry.participationId)
                  if (!voter) return null
                  const note =
                    entry.userId === poll.organizerId
                      ? t('detailLovable.organizerNote')
                      : !entry.isActive
                        ? t('detail.removed')
                        : undefined
                  return (
                    <li
                      key={entry.participationId}
                      data-testid={`poll-roster-entry-${entry.participationId}`}
                    >
                      <VoterRow maskedSuffix={t('detail.hiddenSuffix')} note={note} voter={voter} />
                    </li>
                  )
                })}
              </ul>
            </section>

            {isOrganizer && (isPolling || isAwaitingSelection) && (
              <GuestLinksPanel
                canMint={isPolling}
                clearError={() => setGuestPanelError(null)}
                initialLinks={mintedGuestLinks}
                pollId={pollId}
                roster={detail.data?.roster ?? []}
                onError={setGuestPanelError}
              />
            )}

            <section className="order-6 surface p-4">
              <h3 className="font-display text-base font-extrabold">{t('detail.notificationsHeading')}</h3>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <Button
                  aria-busy={mute.isPending}
                  aria-pressed={poll.muted}
                  disabled={mute.isPending || isTerminal}
                  type="button"
                  variant={poll.muted ? 'primary' : 'neutral'}
                  onClick={() => mute.mutate()}
                >
                  {poll.muted ? t('mute.off') : t('mute.on')}
                </Button>
              </div>
              <p className="mt-2 text-[12px] text-muted-foreground">
                {isTerminal ? t('mute.disabledTerminal') : t('mute.copy')}
              </p>
            </section>

            {canLeave && (
              <section className="order-7 surface p-4">
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
                <p className="mt-2 text-[12px] text-muted-foreground">{t('leave.help')}</p>
              </section>
            )}

            {isOrganizer && (isPolling || isAwaitingSelection) && (
              <section className="order-8 surface border-danger/40 p-4">
                <h3 className="font-display text-base font-extrabold">{t('detail.dangerZone')}</h3>
                <div className="mt-2 flex flex-wrap gap-2">
                  <CancelPollDialog pending={cancel.isPending} onConfirm={() => cancel.mutate()} />
                </div>
              </section>
            )}
          </div>

          <section className="order-2 min-w-0 space-y-3 lg:order-1">
            <div className="surface paper-grain flex flex-wrap items-center justify-between gap-2 p-4">
              <div className="min-w-0">
                <h2 className="font-display text-lg font-extrabold">{t('detailLovable.candidateHeading')}</h2>
                <p className="text-[12px] text-muted-foreground">{t('detailLovable.candidateHint')}</p>
              </div>
              <Button
                className="h-11 rounded-xl font-display font-bold"
                type="button"
                variant="neutral"
                onClick={() => setSuggestOpen(true)}
              >
                <Plus className="size-4" /> {t('suggest.heading')}
              </Button>
            </div>

            <VoteGrid
              disabled={!isPolling}
              formatters={formatters}
              options={poll.options}
              pendingOptionId={pendingOptionId}
              selectedOptionId={poll.selectedOptionId}
              onVote={handleVote}
            />

            <ul className="space-y-3">
              {ranked.map((optionId, index) => {
                const option = poll.options.find((candidate) => candidate.id === optionId)
                if (!option) return null
                const raw = rawOptionsById.get(optionId)
                const isPast = raw ? new Date(raw.startTime).getTime() <= now : false
                return (
                  <li key={optionId}>
                    <OptionCard
                      canVote={isPolling}
                      formatters={formatters}
                      isLeader={optionId === leaderId}
                      isOrganizer={isOrganizer}
                      isPast={isPast}
                      option={option}
                      pendingOptionId={pendingOptionId}
                      poll={poll}
                      rank={index + 1}
                      previewPending={preview.isPending}
                      onDelete={() => setDeleteOptionId(optionId)}
                      onPreview={() => setPreviewOptionId(optionId)}
                      onVote={(choice) => handleVote(optionId, choice)}
                    />
                  </li>
                )
              })}
            </ul>
          </section>
          </main>
        </>
      )}

      {poll && isOrganizer && (isAwaitingSelection || isPolling) && viableEarly.length > 0 && (
        <div className={`fixed inset-x-0 z-40 border-t border-border bg-card/95 backdrop-blur ${BOTTOM_BAR_OFFSET_CLASSES}`}>
          <div className="mx-auto flex max-w-[1400px] items-center gap-2 px-3 py-3 sm:px-6">
            <p className="min-w-0 flex-1 truncate text-[12px] text-muted-foreground">
              {isAwaitingSelection
                ? t('detailLovable.countdown.pickWindow', { time: formatCountdown(pickLeftMin) })
                : t('detailLovable.countdown.votingClosesIn', { time: formatCountdown(deadlineLeftMin) })}
            </p>
            <Button
              aria-busy={preview.isPending}
              className="h-11 rounded-xl font-display font-extrabold"
              data-testid="finalize-trigger"
              disabled={preview.isPending}
              type="button"
              variant="primary"
              onClick={() => setPreviewOptionId(viableEarly[0]?.optionId ?? leaderId)}
            >
              <Trophy className="size-4" /> {t('detailLovable.previewAndFinalize')}
            </Button>
          </div>
        </div>
      )}

      {poll && (
        <FinalizeDialog
          early={isPolling}
          formatters={formatters}
          option={previewOption}
          optionCard={previewOptionCard}
          pending={finalize.isPending}
          poll={poll}
          onClose={() => setPreviewOptionId(null)}
          onConfirm={(optionId) => finalize.mutate(optionId)}
        />
      )}

      <SuggestDialog
        error={suggestError}
        open={suggestOpen}
        pending={suggest.isPending}
        onClose={() => {
          setSuggestOpen(false)
          setSuggestError(null)
        }}
        onAdd={handleSuggest}
      />

      <DeleteOptionDialog
        open={Boolean(deleteOptionCard)}
        pending={deleteOption.isPending}
        onClose={() => setDeleteOptionId(null)}
        onConfirm={() => deleteOptionId && deleteOption.mutate(deleteOptionId)}
      />
    </div>
  )
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-border bg-background px-2.5 py-2">
      <dt className="text-[11px] text-muted-foreground">{label}</dt>
      <dd className="truncate font-display text-[13px] font-extrabold">{value}</dd>
    </div>
  )
}
