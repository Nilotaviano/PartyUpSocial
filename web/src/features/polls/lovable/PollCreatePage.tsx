import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router'
import { ChevronLeft, ChevronRight, Clock3, Gamepad2, Users, Vote as VoteIcon } from 'lucide-react'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import {
  controlRowClasses,
  fieldControlClasses,
  fieldSelectClasses,
  neutralCtaClasses,
  raisedInteractiveClasses,
} from '../../../components/ui/classes'
import { Dialog } from '../../../components/ui/dialog'
import { BOTTOM_BAR_OFFSET_CLASSES } from '../../../components/shell/bottomBarOffset'
import { PageHeader } from '../../../components/shell/PageHeader'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { useDirtyGuard } from '../../../components/useDirtyGuard'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { avatarImgUrl } from '../../friends/avatarImage'
import { friendKeys, getFriends } from '../../friends/friendGraph'
import { getGroupList, groupKeys } from '../../groups/groupQueries'
import { VisibilityControl, type Visibility } from '../../groups/VisibilityControl'
import { getViewerProfile, viewerProfileKey } from '../../sessions/sessionQueries'
import { SteamGamePicker } from '../../sessions/SteamGamePicker'
import { cn } from '../../availability/lovable/utils'
import {
  DAY_MS,
  fakeLocalToInstant,
  instantToFakeLocal,
  startOfLocaleWeek,
  weekStartsOnIso,
} from '../../availability/lovable/zone'
import { mapCalendar } from '../../availability/lovable/calendarData'
import { useIsMobile } from '../../availability/lovable/use-mobile'
import type { WeekLabels } from '../../availability/lovable/partyup-data'
import { availabilityKeys, getCalendarView } from '../../availability/availabilityQueries'
import { CandidateGrid } from './CandidateGrid'
import type { Candidate } from './pollData'
import { createPoll, type CreatePollRequest } from '../pollQueries'

/**
 * Ported from the Lovable prototype's `src/routes/polls.new.tsx` (S3 of the
 * verbatim port), byte-faithful to its section layout (session details ·
 * attendance · voting deadline · candidate grid · sticky action bar) apart
 * from:
 *  - No `AppHeader` (out of scope for this slice).
 *  - The prototype's free-text `game`/`platform` pair is the real Steam-arm
 *    game field (D-176/D-194, `SteamGamePicker`) plus a `Platform` select —
 *    absorbed near-verbatim from the pre-port `PollCreatePage`.
 *  - The prototype's own `Notes`/`Minimum`/`Maximum capacity` controls are the
 *    real validated fields (min/max range checks), not decorative segmented
 *    pickers. There is no duration field at all: D-211 gives every candidate
 *    its own length, painted on the grid below.
 *  - The prototype's three-state `Privacy` (`friends`/`invite`/`public`) is
 *    the real three-level `visibility` (Private/Groups/Public,
 *    `VisibilityControl` from `features/groups`), with the real invitee
 *    checklist and guest-link count beneath it (D-125: a public poll needs
 *    at least one invited friend; D-201: a Groups poll needs at least one
 *    targeted group).
 *  - Candidate times are S1's `CandidateGrid`, not the prototype's own copy.
 *    The grid only shows one week at a time, so — unlike the prototype,
 *    which had no week paging — this page adds Prev/Next-week controls so
 *    any future date stays reachable. The
 *    grid's `friends` prop is the same `GET /calendar` friend-availability
 *    layer the calendar shell paints (`mapCalendar`, reused rather than
 *    duplicated), refetched for the displayed week as it's paged — so the
 *    grid highlights slots against real friend windows instead of always
 *    rendering the neutral, unhighlighted state, and the same response's
 *    `sessions` are painted as pills underneath. A failed fetch soft-fails
 *    to that same neutral grid (`mapCalendar(undefined, ...)` already
 *    returns empty `friends`/`sessions` arrays) rather than toasting on this
 *    page. The mobile 3-day pager mirrors `CalendarShell`'s.
 *  - Every user-facing string moves through `useTranslation('polls')`.
 */

const MAX_OPTIONS = 12
const MIN_OPTIONS = 2

// D-130's minimum lead, mirrored client-side, matching the pre-port page.
const MINIMUM_LEAD_MS = 15 * 60_000

/**
 * D-211: an option is a start *and* a length. `durationMinutes` starts at the
 * grid's own default and is whatever the organizer painted or resized it to.
 */
type PollOptionDraft = { start: string; durationMinutes: number }

const DEFAULT_OPTION_MINUTES = 60

function sortDrafts(drafts: PollOptionDraft[]) {
  return [...drafts].sort((a, b) => a.start.localeCompare(b.start))
}

function readPollPrefill(
  params: URLSearchParams,
): { options: PollOptionDraft[]; anyClamped: boolean; anyTruncated: boolean } {
  const raw = params.getAll('startTimes')
  if (raw.length === 0) return { options: [], anyClamped: false, anyTruncated: false }

  const earliest = Date.now() + MINIMUM_LEAD_MS
  const deduped: string[] = []
  let anyClamped = false

  for (const value of raw) {
    const requested = new Date(value)
    if (Number.isNaN(requested.getTime())) continue
    const clamped = requested.getTime() < earliest
    if (clamped) anyClamped = true
    const iso = new Date(clamped ? earliest : requested.getTime()).toISOString()
    if (!deduped.includes(iso)) {
      deduped.push(iso)
    }
  }

  const sorted = deduped.sort()
  return {
    options: sorted
      .slice(0, MAX_OPTIONS)
      .map((start) => ({ start, durationMinutes: DEFAULT_OPTION_MINUTES })),
    anyClamped,
    anyTruncated: sorted.length > MAX_OPTIONS,
  }
}

const fieldIds: Record<string, string> = {
  title: 'field-title',
  options: 'field-options',
  minAttendance: 'field-min',
  maxCapacity: 'field-max',
  inviteeUserIds: 'field-invitees',
  audienceGroupIds: 'field-visibility',
  guestLinkCount: 'field-guest-links',
  gameAppId: 'field-game-app-id',
  gameName: 'field-game-name',
}

const platformWireValues: Record<string, string> = {
  Steam: 'steam',
  PlayStation: 'playStation',
  Xbox: 'xbox',
  Other: 'notApplicable',
}

const deadlineChoices = ['hours6', 'hours12', 'hours24', 'hours48'] as const

const emptyForm = {
  title: '',
  options: [] as PollOptionDraft[],
  deadlineChoice: 'hours24' as (typeof deadlineChoices)[number],
  platform: '',
  gameName: '',
  gameAppId: '',
  notes: '',
  minAttendance: '4',
  maxCapacity: '4',
  unlimited: false,
  // D-129: default visibility is Public on create.
  visibility: 'public' as Visibility,
  audienceGroupIds: [] as string[],
  inviteeUserIds: [] as string[],
  guestLinkCount: '0',
}

function Section({
  icon: Icon,
  heading,
  children,
}: {
  icon: typeof Gamepad2
  heading: string
  children: ReactNode
}) {
  return (
    <section className="surface paper-grain space-y-3 p-4">
      <h2 className="flex items-center gap-2 font-display text-base font-extrabold text-ink">
        <Icon className="size-4" /> {heading}
      </h2>
      {children}
    </section>
  )
}

function SegmentedGroup<T extends string>({
  options,
  value,
  onChange,
  ariaLabel,
}: {
  options: { value: T; label: string }[]
  value: T
  onChange: (v: T) => void
  ariaLabel: string
}) {
  return (
    <div
      aria-label={ariaLabel}
      className="grid gap-1 rounded-xl bg-muted p-1"
      role="radiogroup"
      style={{ gridTemplateColumns: `repeat(${options.length}, minmax(0,1fr))` }}
    >
      {options.map((o) => (
        <button
          key={o.value}
          aria-checked={value === o.value}
          className={cn(
            'h-11 rounded-lg font-display text-[13px] font-bold transition-colors',
            value === o.value
              ? 'bg-card text-ink shadow-[var(--shadow-card)]'
              : 'text-ink-soft',
          )}
          role="radio"
          type="button"
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

type DiscardDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onDiscard: () => void
  triggerRef: React.RefObject<HTMLElement | null>
}

function PollDiscardDialog({ open, onOpenChange, onDiscard, triggerRef }: DiscardDialogProps) {
  const { t } = useTranslation('polls')
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-discard-dialog-description-lovable"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('dirty.title')}
          </Dialog.Title>
          <Dialog.Description
            className="mt-2 text-ink-soft"
            id="poll-discard-dialog-description-lovable"
          >
            {t('dirty.body')}
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
            <Button type="button" variant="danger" onClick={onDiscard}>
              {t('dirty.discard')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export function PollCreatePage() {
  const { t, i18n } = useTranslation(['polls', 'errors', 'availability'])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const summaryRef = useRef<HTMLDivElement>(null)
  const cancelTrigger = useRef<HTMLButtonElement>(null)
  const [searchParams] = useSearchParams()
  const [prefill] = useState(() => readPollPrefill(searchParams))
  const [form, setForm] = useState(() =>
    prefill.options.length > 0 ? { ...emptyForm, options: prefill.options } : emptyForm,
  )
  const [dirty, setDirty] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [discardOpen, setDiscardOpen] = useState(false)
  const [pendingNavigation, setPendingNavigation] = useState<string | null>(null)
  const [weekOffset, setWeekOffset] = useState(0)
  const [earlyHours, setEarlyHours] = useState(false)
  // `null` = no explicit paging yet; the window then starts at the week's
  // first day, the same clamped 0..4 window `CalendarShell` pages over.
  const [pageStart, setPageStart] = useState<number | null>(null)
  const isMobile = useIsMobile()

  const optionFormat = useMemo(
    () => new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' }),
    [i18n.language],
  )
  const endFormat = useMemo(
    () => new Intl.DateTimeFormat(i18n.language, { timeStyle: 'short' }),
    [i18n.language],
  )

  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const timeZone = profile.data?.timeZone ?? 'UTC'

  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })

  // D-201: any group the actor owns or holds an Accepted membership in can target.
  const groupList = useQuery({ queryKey: groupKeys.list, queryFn: getGroupList })
  const targetableGroups = [
    ...(groupList.data?.owned ?? []),
    ...(groupList.data?.memberOf ?? []),
  ].map((group) => ({ id: group.id, name: group.name }))

  const submit = useMutation({
    mutationFn: (body: CreatePollRequest) => createPoll(body),
    onSuccess: async (response) => {
      await queryClient.invalidateQueries({ queryKey: ['polls'] })
      // The mint response's guest links are only ever returned here — tokens are
      // stored hashed server-side and there is no GET to re-list them, so they
      // are handed to the detail page as one-shot router state rather than
      // dropped. `GuestLinksPanel` renders them once and warns that a refresh
      // loses them.
      if (response.guestLinks.length > 0) {
        navigate(`/polls/${response.poll.id}`, {
          state: { mintedGuestLinks: response.guestLinks },
        })
      } else {
        navigate(`/polls/${response.poll.id}`)
      }
    },
  })

  useDirtyGuard(dirty, {
    onBlockedNavigation: (target) => {
      setPendingNavigation(target)
      setDiscardOpen(true)
    },
  })

  const weekStart = useMemo(() => {
    const zonedNow = instantToFakeLocal(new Date().toISOString(), timeZone)
    const anchor = new Date(zonedNow.getTime() + weekOffset * 7 * DAY_MS)
    return startOfLocaleWeek(anchor, weekStartsOnIso(i18n.language))
  }, [timeZone, weekOffset, i18n.language])

  const labels: WeekLabels = useMemo(() => {
    const shortDay = new Intl.DateTimeFormat(i18n.language, { weekday: 'short', timeZone: 'UTC' })
    const longDay = new Intl.DateTimeFormat(i18n.language, { weekday: 'long', timeZone: 'UTC' })
    const date = new Intl.DateTimeFormat(i18n.language, {
      day: 'numeric',
      month: 'short',
      timeZone: 'UTC',
    })
    const dates = Array.from({ length: 7 }, (_, i) => new Date(weekStart.getTime() + i * DAY_MS))
    return {
      dayNames: dates.map((d) => shortDay.format(d)),
      dayLongNames: dates.map((d) => longDay.format(d)),
      dayDates: dates.map((d) => date.format(d)),
    }
  }, [weekStart, i18n.language])

  const weekRangeLabel = useMemo(() => {
    const last = new Date(weekStart.getTime() + 6 * DAY_MS)
    const dayMonth = new Intl.DateTimeFormat(i18n.language, {
      day: 'numeric',
      month: 'short',
      timeZone: 'UTC',
    })
    return dayMonth.formatRange(weekStart, last)
  }, [weekStart, i18n.language])

  // Same `from`/`to` derivation `CalendarShell` uses for its own week fetch —
  // the whole displayed week, in the viewer's zone.
  const from = fakeLocalToInstant(weekStart, timeZone)
  const to = fakeLocalToInstant(new Date(weekStart.getTime() + 7 * DAY_MS), timeZone)

  const calendarView = useQuery({
    queryKey: availabilityKeys.calendar(from, to),
    queryFn: () => getCalendarView(from, to),
    enabled: Boolean(profile.data),
  })

  // Errors soft-fail here: `mapCalendar(undefined, ...)` already returns an
  // empty `friends` array, so a failed fetch just leaves the grid neutral —
  // this create page has no toast wired to this query, unlike the calendar
  // shell. `voteLabel` is unused (the grid never renders poll pills) but
  // `mapCalendar` is reused whole rather than partly duplicated.
  const calendarLayers = useMemo(
    () =>
      mapCalendar(calendarView.data, weekStart, timeZone, {
        anonymousFriend: t('calendar.anonymousFriend', { ns: 'availability' }),
        voteLabel: () => '',
      }),
    [calendarView.data, weekStart, timeZone, t],
  )

  const pageStartValue = Math.max(0, Math.min(pageStart ?? 0, 4))
  const visibleDays = useMemo(
    () =>
      isMobile
        ? [pageStartValue, pageStartValue + 1, pageStartValue + 2]
        : [0, 1, 2, 3, 4, 5, 6],
    [isMobile, pageStartValue],
  )

  function stepDays(delta: -1 | 1) {
    setPageStart(Math.max(0, Math.min(pageStartValue + delta, 4)))
  }

  const candidatesInView: Candidate[] = useMemo(() => {
    return form.options.flatMap((option) => {
      const fakeLocal = instantToFakeLocal(option.start, timeZone)
      const dayIndex = Math.floor((fakeLocal.getTime() - weekStart.getTime()) / DAY_MS)
      if (dayIndex < 0 || dayIndex > 6) return []
      const start = fakeLocal.getUTCHours() * 60 + fakeLocal.getUTCMinutes()
      return [{ day: dayIndex, start, durationMinutes: option.durationMinutes }]
    })
  }, [form.options, weekStart, timeZone])

  function candidateToIso(c: Candidate) {
    const fakeLocal = new Date(weekStart.getTime() + c.day * DAY_MS + c.start * 60_000)
    return fakeLocalToInstant(fakeLocal, timeZone)
  }

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((current) => ({ ...current, [key]: value }))
    setDirty(true)
  }

  function clearOptionsError() {
    setFieldErrors((current) => {
      if (!('options' in current)) return current
      const rest = { ...current }
      delete rest.options
      return rest
    })
  }

  // D-080: identical instants merge, never a validation error — painting or
  // tapping a start another candidate already holds keeps the one already
  // there rather than producing a duplicate, and the second gesture's length
  // wins because it is the more recent expression of intent.
  function handleCandidateCreate(c: Candidate) {
    const iso = candidateToIso(c)
    const existing = form.options.some((o) => o.start === iso)
    if (!existing && form.options.length >= MAX_OPTIONS) {
      setFieldErrors((current) => ({ ...current, options: t('validation.optionCap') }))
      return
    }
    clearOptionsError()
    setForm((current) => ({
      ...current,
      options: sortDrafts([
        ...current.options.filter((o) => o.start !== iso),
        { start: iso, durationMinutes: c.durationMinutes },
      ]),
    }))
    setDirty(true)
  }

  function handleCandidateRemove(c: Candidate) {
    removeOption(candidateToIso(c))
  }

  // Dragging a band to a time another candidate already holds is the same
  // D-080 merge a repeated tap is: the moved instant collapses into the one
  // already there rather than ever producing a duplicate.
  function handleCandidateMove(from: Candidate, to: Candidate) {
    const fromIso = candidateToIso(from)
    const toIso = candidateToIso(to)
    clearOptionsError()
    setForm((current) => {
      const without = current.options.filter((o) => o.start !== fromIso)
      return {
        ...current,
        options: sortDrafts([
          ...without.filter((o) => o.start !== toIso),
          { start: toIso, durationMinutes: to.durationMinutes },
        ]),
      }
    })
    setDirty(true)
  }

  // D-211: a resize edits the one option the handle belongs to, never the rest.
  function handleCandidateResize(c: Candidate, durationMinutes: number) {
    const iso = candidateToIso(c)
    setForm((current) => ({
      ...current,
      options: current.options.map((o) => (o.start === iso ? { ...o, durationMinutes } : o)),
    }))
    setDirty(true)
  }

  function removeOption(iso: string) {
    setForm((current) => ({
      ...current,
      options: current.options.filter((option) => option.start !== iso),
    }))
    setDirty(true)
  }

  function validate(): Record<string, string> {
    const errors: Record<string, string> = {}
    if (!form.title.trim()) errors.title = t('validation.required')
    if (form.options.length < MIN_OPTIONS) errors.options = t('validation.optionMinimum')
    if (form.options.length > MAX_OPTIONS) errors.options = t('validation.optionCap')
    // D-131 is now a per-option rule: every band carries its own length. The
    // count errors above take display priority; don't overwrite them.
    if (
      !errors.options &&
      form.options.some(
        (option) =>
          !Number.isFinite(option.durationMinutes) ||
          option.durationMinutes <= 0 ||
          option.durationMinutes % 15 !== 0,
      )
    ) {
      errors.options = t('validation.increment')
    }
    const min = Number(form.minAttendance)
    const max = form.unlimited ? undefined : Number(form.maxCapacity)
    if (!Number.isFinite(min) || min < 1) errors.minAttendance = t('validation.range')
    if (!form.unlimited && (!Number.isFinite(max) || (max as number) < 1)) {
      errors.maxCapacity = t('validation.range')
    }
    if (!form.unlimited && max !== undefined && min > max) {
      errors.minAttendance = t('validation.minMax')
    }
    // D-125: a public poll needs at least one directly invited registered friend.
    if (form.visibility === 'public' && form.inviteeUserIds.length === 0) {
      errors.inviteeUserIds = t('validation.invitedFriend')
    }
    if (form.visibility === 'groups' && form.audienceGroupIds.length === 0) {
      errors.audienceGroupIds = t('validation.pickAtLeastOneGroup')
    }
    if (form.inviteeUserIds.length > 50) {
      errors.inviteeUserIds = t('validation.tooManyInvitees')
    }
    const guestLinkCount = Number(form.guestLinkCount)
    if (guestLinkCount > 20) {
      errors.guestLinkCount = t('validation.tooManyGuestLinks')
    }
    if (form.platform === 'Steam' && form.gameAppId && !/^\d+$/.test(form.gameAppId)) {
      errors.gameAppId = t('validation.steamAppIdDigits')
    }
    if (form.platform === 'Steam' && form.gameAppId && !form.gameName.trim()) {
      errors.gameName = t('validation.steamAppIdNeedsGameName')
    }
    return errors
  }

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault()
    const errors = validate()
    setFieldErrors(errors)
    if (Object.keys(errors).length > 0) {
      summaryRef.current?.focus()
      return
    }

    const game = form.gameName
      ? {
          name: form.gameName,
          steamAppId: form.platform === 'Steam' && form.gameAppId ? form.gameAppId : null,
        }
      : null

    submit.mutate({
      title: form.title,
      options: form.options.map((option) => ({
        startTime: option.start,
        durationMinutes: option.durationMinutes,
      })),
      deadlineChoice: form.deadlineChoice,
      platform: form.platform ? platformWireValues[form.platform] : null,
      game,
      notes: form.notes || null,
      minAttendance: Number(form.minAttendance),
      maxCapacity: form.unlimited ? null : Number(form.maxCapacity),
      visibility: form.visibility,
      audienceGroupIds: form.visibility === 'groups' ? form.audienceGroupIds : [],
      inviteeUserIds: form.inviteeUserIds.length > 0 ? form.inviteeUserIds : null,
      guestLinkCount: guestLinkCountOf(form.guestLinkCount),
    })
  }

  function guestLinkCountOf(value: string) {
    const parsed = Number(value)
    return parsed > 0 ? parsed : null
  }

  function requestCancel() {
    if (dirty) {
      setPendingNavigation('/polls')
      setDiscardOpen(true)
      return
    }
    navigate('/polls')
  }

  return (
    <main className="min-h-screen bg-background pb-32" data-testid="screen-poll-create">
      <div className="mx-auto max-w-[1400px] px-3 sm:px-6">
        <PageHeader back={{ label: t('nav.backToList'), to: '/polls' }} title={t('create.heading')} />
      </div>

      {prefill.anyClamped && (
        <p aria-live="polite" className="mx-auto max-w-[1400px] px-3 sm:px-6" role="status">
          {t('prefill.clamped', { ns: 'availability' })}
        </p>
      )}

      {prefill.anyTruncated && (
        <p aria-live="polite" className="mx-auto max-w-[1400px] px-3 sm:px-6" role="status">
          {t('prefill.pollTruncated', { max: MAX_OPTIONS })}
        </p>
      )}

      {submit.error && (
        <div className="mx-auto max-w-[1400px] px-3 sm:px-6">
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(submit.error) ? `errors.codes.${submit.error.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        </div>
      )}

      <form onSubmit={handleSubmit}>
        {Object.keys(fieldErrors).length > 0 && (
          <div className="mx-auto max-w-[1400px] px-3 sm:px-6">
            <div
              ref={summaryRef}
              className="rounded-card border-2 border-danger bg-danger/10 p-4"
              role="alert"
              tabIndex={-1}
            >
              <p className="font-display font-semibold text-ink">{t('a11y.validationSummary')}</p>
              <ul className="mt-2 list-disc pl-5 text-ink">
                {Object.entries(fieldErrors).map(([field, message]) => (
                  <li key={field}>
                    <a
                      className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                      href={`#${fieldIds[field] ?? `field-${field}`}`}
                    >
                      {message}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          </div>
        )}

        <div className="mx-auto grid max-w-[1400px] gap-4 px-3 py-4 sm:px-6 lg:grid-cols-[360px_minmax(0,1fr)]">
          {/* details */}
          <div className="space-y-4">
            <Section heading={t('create.heading')} icon={Gamepad2}>
              <label className="block" htmlFor="field-title">
                <span className="text-[12px] font-bold text-ink-soft">{t('create.title')}</span>
                <input
                  aria-describedby={fieldErrors.title ? 'title-error' : undefined}
                  aria-invalid={Boolean(fieldErrors.title)}
                  className={fieldControlClasses}
                  id="field-title"
                  required
                  value={form.title}
                  onChange={(event) => update('title', event.target.value)}
                />
              </label>
              {fieldErrors.title && (
                <p id="title-error" role="alert">
                  {fieldErrors.title}
                </p>
              )}

              <label className="block" htmlFor="field-platform">
                <span className="text-[12px] font-bold text-ink-soft">{t('create.platform')}</span>
                <select
                  className={fieldSelectClasses}
                  id="field-platform"
                  value={form.platform}
                  onChange={(event) => update('platform', event.target.value)}
                >
                  <option value="">—</option>
                  <option value="Steam">Steam</option>
                  <option value="PlayStation">PlayStation</option>
                  <option value="Xbox">Xbox</option>
                  <option value="Other">{t('create.platformOther')}</option>
                </select>
              </label>

              <fieldset className="flex flex-col gap-1.5">
                <legend className="text-[12px] font-bold text-ink-soft">{t('create.game')}</legend>
                {form.platform === 'Steam' ? (
                  <SteamGamePicker
                    appIdError={fieldErrors.gameAppId}
                    appIdId="field-game-app-id"
                    appIdValue={form.gameAppId}
                    nameError={fieldErrors.gameName}
                    nameId="field-game-name"
                    nameValue={form.gameName}
                    t={t}
                    onAppIdChange={(appId) => update('gameAppId', appId)}
                    onNameChange={(name) => update('gameName', name)}
                    onSelect={({ name, appId }) => {
                      setForm((current) => ({ ...current, gameName: name, gameAppId: appId }))
                      setDirty(true)
                    }}
                  />
                ) : (
                  <>
                    <label className="block" htmlFor="field-game-name">
                      {t('create.gameFreeText')}
                    </label>
                    <input
                      aria-describedby={fieldErrors.gameName ? 'game-name-error' : undefined}
                      aria-invalid={Boolean(fieldErrors.gameName)}
                      className={fieldControlClasses}
                      id="field-game-name"
                      value={form.gameName}
                      onChange={(event) => update('gameName', event.target.value)}
                    />
                    {fieldErrors.gameName && (
                      <p id="game-name-error" role="alert">
                        {fieldErrors.gameName}
                      </p>
                    )}
                  </>
                )}
              </fieldset>

              <label className="block" htmlFor="field-notes">
                <span className="text-[12px] font-bold text-ink-soft">{t('create.notes')}</span>
                <textarea
                  className={fieldControlClasses}
                  id="field-notes"
                  value={form.notes}
                  onChange={(event) => update('notes', event.target.value)}
                />
              </label>
            </Section>

            <Section heading={t('create.invitees')} icon={Users}>
              <div className="grid gap-3 sm:grid-cols-2">
                <label className="block" htmlFor="field-min">
                  <span className="text-[12px] font-bold text-ink-soft">{t('create.min')}</span>
                  <input
                    aria-describedby={fieldErrors.minAttendance ? 'min-error' : undefined}
                    aria-invalid={Boolean(fieldErrors.minAttendance)}
                    className={fieldControlClasses}
                    id="field-min"
                    min={1}
                    type="number"
                    value={form.minAttendance}
                    onChange={(event) => update('minAttendance', event.target.value)}
                  />
                  {fieldErrors.minAttendance && (
                    <p id="min-error" role="alert">
                      {fieldErrors.minAttendance}
                    </p>
                  )}
                </label>
                <label className="block" htmlFor="field-max">
                  <span className="text-[12px] font-bold text-ink-soft">{t('create.max')}</span>
                  <input
                    aria-describedby={fieldErrors.maxCapacity ? 'max-error' : undefined}
                    aria-invalid={Boolean(fieldErrors.maxCapacity)}
                    className={fieldControlClasses}
                    disabled={form.unlimited}
                    id="field-max"
                    min={1}
                    type="number"
                    value={form.maxCapacity}
                    onChange={(event) => update('maxCapacity', event.target.value)}
                  />
                  {fieldErrors.maxCapacity && (
                    <p id="max-error" role="alert">
                      {fieldErrors.maxCapacity}
                    </p>
                  )}
                </label>
              </div>
              <label className={controlRowClasses}>
                <input
                  checked={form.unlimited}
                  type="checkbox"
                  onChange={(event) => update('unlimited', event.target.checked)}
                />
                {t('create.unlimited')}
              </label>

              <div id="field-visibility">
                <VisibilityControl
                  groups={targetableGroups}
                  selectedGroupIds={form.audienceGroupIds}
                  value={form.visibility}
                  onChange={(visibility) => update('visibility', visibility)}
                  onToggleGroup={(groupId) =>
                    update(
                      'audienceGroupIds',
                      form.audienceGroupIds.includes(groupId)
                        ? form.audienceGroupIds.filter((id) => id !== groupId)
                        : [...form.audienceGroupIds, groupId],
                    )
                  }
                />
                {fieldErrors.audienceGroupIds && (
                  <p role="alert">{fieldErrors.audienceGroupIds}</p>
                )}
              </div>

              <fieldset className="flex flex-col gap-2" id="field-invitees">
                <legend className="text-[12px] font-bold text-ink-soft">
                  {t('create.invitees')}
                </legend>
                {fieldErrors.inviteeUserIds && <p role="alert">{fieldErrors.inviteeUserIds}</p>}
                {friends.isPending && (
                  <p className="motion-safe:animate-pulse text-ink-soft" role="status">
                    {t('common.loading', { ns: 'common' })}
                  </p>
                )}
                {friends.isError && (
                  <p className="text-ink-soft">{t('invite.friendsLoadFailed')}</p>
                )}
                {friends.data && friends.data.length === 0 && (
                  <p className="text-ink-soft">{t('invite.noFriends')}</p>
                )}
                {friends.data && friends.data.length > 0 && (
                  <ul className="flex flex-col gap-2">
                    {friends.data.map((friend) => (
                      <li key={friend.friendshipId}>
                        <label className={controlRowClasses}>
                          <input
                            checked={form.inviteeUserIds.includes(friend.userId)}
                            type="checkbox"
                            onChange={(event) => {
                              const checked = event.target.checked
                              update(
                                'inviteeUserIds',
                                checked
                                  ? [...form.inviteeUserIds, friend.userId]
                                  : form.inviteeUserIds.filter((id) => id !== friend.userId),
                              )
                            }}
                          />
                          <AvatarDisc
                            emoji={friend.avatar.emoji}
                            imgUrl={avatarImgUrl(friend.avatar)}
                            seed={friend.userId}
                            size="xs"
                          />
                          {friend.displayName} (@{friend.username})
                        </label>
                      </li>
                    ))}
                  </ul>
                )}
              </fieldset>

              <label className="block" htmlFor="field-guest-links">
                <span className="text-[12px] font-bold text-ink-soft">
                  {t('create.guestLinks')}
                </span>
                <input
                  aria-describedby={
                    fieldErrors.guestLinkCount ? 'guest-links-error' : undefined
                  }
                  className={fieldControlClasses}
                  id="field-guest-links"
                  max={20}
                  min={0}
                  type="number"
                  value={form.guestLinkCount}
                  onChange={(event) => update('guestLinkCount', event.target.value)}
                />
              </label>
              {fieldErrors.guestLinkCount && (
                <p id="guest-links-error" role="alert">
                  {fieldErrors.guestLinkCount}
                </p>
              )}
            </Section>

            <Section heading={t('create.deadline')} icon={Clock3}>
              <SegmentedGroup
                ariaLabel={t('create.deadline')}
                options={deadlineChoices.map((choice) => ({
                  value: choice,
                  label: t(`create.deadlineChoice.${choice}`),
                }))}
                value={form.deadlineChoice}
                onChange={(choice) => update('deadlineChoice', choice)}
              />
            </Section>
          </div>

          {/* candidate picking surface */}
          <section className="min-w-0 space-y-3">
            <div className="surface paper-grain p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="min-w-0">
                  <h2 className="font-display text-base font-extrabold text-ink">
                    {t('create.grid.heading')}
                  </h2>
                  <p className="text-[12px] text-ink-soft">{t('create.grid.hint')}</p>
                </div>
                <span
                  className={cn(
                    'inline-flex h-11 items-center gap-1.5 rounded-xl border px-3 font-display text-[13px] font-extrabold tabular-nums',
                    form.options.length >= MIN_OPTIONS
                      ? 'border-free-foreground/40 bg-free text-free-foreground'
                      : 'border-border bg-muted text-ink-soft',
                  )}
                >
                  <VoteIcon className="size-4" />
                  {form.options.length}/{MAX_OPTIONS}
                </span>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <button
                  className="inline-flex h-11 items-center gap-2 rounded-xl border border-border px-3 text-[13px] font-semibold text-ink"
                  type="button"
                  onClick={() => setEarlyHours((v) => !v)}
                >
                  <Clock3 className="size-4" />
                  {earlyHours ? t('create.grid.eveningsOnly') : t('create.grid.showDaytime')}
                </button>
                <button
                  aria-label={t('calendar.previousWeek', { ns: 'availability' })}
                  className="inline-flex size-11 items-center justify-center rounded-xl border border-border"
                  type="button"
                  onClick={() => setWeekOffset((value) => value - 1)}
                >
                  <ChevronLeft className="size-4" />
                </button>
                <span className="font-display text-[12px] font-bold text-ink-soft">
                  {t('create.grid.weekOf', { range: weekRangeLabel })}
                </span>
                <button
                  aria-label={t('calendar.nextWeek', { ns: 'availability' })}
                  className="inline-flex size-11 items-center justify-center rounded-xl border border-border"
                  type="button"
                  onClick={() => setWeekOffset((value) => value + 1)}
                >
                  <ChevronRight className="size-4" />
                </button>
              </div>

              {/* mobile day pager — the calendar's own 3-day window */}
              <div className="mt-3 md:hidden" data-testid="candidate-day-pager">
                <div className="grid grid-cols-7 gap-1">
                  {labels.dayNames.map((name, index) => (
                    <button
                      key={`${name}-${index}`}
                      aria-pressed={visibleDays.includes(index)}
                      className={cn(
                        'h-11 rounded-xl border font-display text-[12px] font-bold',
                        visibleDays.includes(index)
                          ? 'border-primary bg-primary/20 text-ink'
                          : 'border-border bg-background text-ink-soft',
                      )}
                      type="button"
                      onClick={() => setPageStart(Math.max(0, Math.min(index, 4)))}
                    >
                      {name}
                    </button>
                  ))}
                </div>
                <div className="mt-1.5 flex items-center gap-1.5">
                  <button
                    aria-label={t('create.grid.earlierDays')}
                    className="inline-flex size-11 shrink-0 items-center justify-center rounded-xl border border-border"
                    type="button"
                    onClick={() => stepDays(-1)}
                  >
                    <ChevronLeft className="size-4" />
                  </button>
                  <p className="min-w-0 flex-1 truncate text-center font-display text-[12px] font-bold text-ink-soft">
                    {t('create.grid.dayPagerRange', {
                      start: labels.dayNames[visibleDays[0]!],
                      end: labels.dayNames[visibleDays[visibleDays.length - 1]!],
                    })}
                  </p>
                  <button
                    aria-label={t('create.grid.laterDays')}
                    className="inline-flex size-11 shrink-0 items-center justify-center rounded-xl border border-border"
                    type="button"
                    onClick={() => stepDays(1)}
                  >
                    <ChevronRight className="size-4" />
                  </button>
                </div>
              </div>
            </div>

            <div className="surface p-2 sm:p-3" data-testid="candidate-grid">
              <CandidateGrid
                candidates={candidatesInView}
                capReached={form.options.length >= MAX_OPTIONS}
                days={visibleDays}
                friends={calendarLayers.friends}
                labels={labels}
                max={MAX_OPTIONS}
                rangeEnd={24 * 60}
                rangeStart={earlyHours ? 10 * 60 : 16 * 60}
                sessions={calendarLayers.sessions}
                onCreate={handleCandidateCreate}
                onMove={handleCandidateMove}
                onPageStep={isMobile ? stepDays : undefined}
                onRemove={handleCandidateRemove}
                onResize={handleCandidateResize}
              />
            </div>

            <fieldset className="surface flex flex-col gap-1.5 p-4" id="field-options">
              <legend className="font-display text-[13px] font-extrabold text-ink">
                {t('create.options')}
              </legend>
              <p className="font-body text-meta text-ink-soft">{t('create.optionsHelp')}</p>
              {fieldErrors.options && (
                <p id="options-error" role="alert">
                  {fieldErrors.options}
                </p>
              )}
              {form.options.length > 0 && (
                <ul>
                  {form.options.map((option) => {
                    // D-211: the option owns its end, so the row states the
                    // whole window rather than a bare start.
                    const label = `${optionFormat.format(new Date(option.start))} – ${endFormat.format(
                      new Date(new Date(option.start).getTime() + option.durationMinutes * 60_000),
                    )}`
                    return (
                      <li
                        key={option.start}
                        className="flex items-center justify-between gap-2 border-t border-ink/25 py-2 first:border-t-0"
                      >
                        <span className="text-ink">{label}</span>
                        <Button
                          aria-label={`${t('create.removeOption')} ${label}`}
                          type="button"
                          variant="ghost"
                          onClick={() => removeOption(option.start)}
                        >
                          {t('create.removeOption')}
                        </Button>
                      </li>
                    )
                  })}
                </ul>
              )}
            </fieldset>
          </section>
        </div>

        {/* sticky action bar */}
        <div className={`fixed inset-x-0 z-40 border-t border-border bg-card/95 backdrop-blur ${BOTTOM_BAR_OFFSET_CLASSES}`}>
          <div className="mx-auto flex max-w-[1400px] items-center gap-2 px-3 py-3 sm:px-6">
            <p className="min-w-0 flex-1 truncate text-[12px] text-ink-soft">
              {t('create.grid.count', { count: form.options.length, max: MAX_OPTIONS })}
            </p>
            <button
              ref={cancelTrigger}
              className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
              type="button"
              onClick={requestCancel}
            >
              {t('dirty.stay')}
            </button>
            <Button
              aria-busy={submit.isPending}
              disabled={submit.isPending}
              type="submit"
              variant="primary"
            >
              {t('create.submit')}
            </Button>
          </div>
        </div>
      </form>

      <PollDiscardDialog
        open={discardOpen}
        triggerRef={cancelTrigger}
        onDiscard={() => {
          setDiscardOpen(false)
          setDirty(false)
          const target = pendingNavigation ?? '/polls'
          setPendingNavigation(null)
          if (target === 'back') {
            window.history.back()
          } else {
            navigate(target)
          }
        }}
        onOpenChange={setDiscardOpen}
      />
    </main>
  )
}
