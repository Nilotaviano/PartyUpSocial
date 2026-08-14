import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  raisedInteractiveClasses,
  segmentItemClasses,
  segmentStripClasses,
} from '../../components/ui/classes'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
import { Toast } from '../../components/ui/toast'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import { AgendaView, type AgendaEntry } from './AgendaView'
import {
  AvailabilitySummary,
  type AvailabilityOneOff,
} from './AvailabilitySummary'
import { BlockEditorDialog, type BlockEditorValue } from './BlockEditorDialog'
import { CalendarLayers, type CalendarLayerState } from './CalendarLayers'
import { CommitBlockDialog, type CommitRecurrence } from './CommitBlockDialog'
import {
  OccurrenceScopeDialog,
  type OccurrenceScope,
} from './OccurrenceScopeDialog'
import {
  WeekGrid,
  type GridAdjustment,
  type GridFriendRange,
  type GridOccurrence,
  type GridPollOptionEntry,
  type GridRange,
  type GridSessionEntry,
  type GridViewerRelation,
} from './WeekGrid'
import {
  availabilityKeys,
  createAvailabilityBlock,
  createAvailabilitySeries,
  deleteAvailabilityBlock,
  deleteAvailabilityOccurrence,
  deleteAvailabilitySeries,
  editAvailabilityBlock,
  editAvailabilityOccurrence,
  getCalendarView,
  invalidateAvailabilityQueries,
} from './availabilityQueries'

const DAY_MS = 24 * 60 * 60 * 1000

// The grid renders one row per 15 minutes, so a full day is 96 rows. The
// default window is 08:00–24:00 — 64 of them, rendered at natural height for
// the page to scroll — and the 32 early rows stay one control away rather than
// being unreachable.
const WAKING_START_HOUR = 8
const END_HOUR = 24

const DAYS_PER_WEEK = 7
// CALENDAR_DESIGN.md §9 S5: below `sm` the grid shows three of the displayed
// week's days at a time; the same `sm` boundary `CommitBlockDialog` switches
// its popover/bottom-sheet skin on.
const MOBILE_MEDIA_QUERY = '(max-width: 639px)'
const MOBILE_DAY_WINDOW_LENGTH = 3

function clamp(value: number, low: number, high: number) {
  return Math.min(Math.max(value, low), high)
}

function useIsMobileViewport(): boolean {
  const [isMobile, setIsMobile] = useState(() =>
    typeof matchMedia === 'undefined' ? false : matchMedia(MOBILE_MEDIA_QUERY).matches,
  )

  useEffect(() => {
    if (typeof matchMedia === 'undefined') return
    const media = matchMedia(MOBILE_MEDIA_QUERY)
    const listener = () => setIsMobile(media.matches)
    media.addEventListener('change', listener)
    return () => media.removeEventListener('change', listener)
  }, [])

  return isMobile
}

// The five new §8 problem codes (design §10.2): each renders its own
// `availability.json` message. Every other code — including the two shared
// `rate_limited`/`transient_conflict` — reads from the shipped flat
// `errors` namespace with its `generic` fallback (the guest-page convention).
const AVAILABILITY_PROBLEM_CODES = new Set([
  'availability_not_found',
  'availability_validation',
  'invalid_range',
  'range_too_large',
  'occurrence_not_found',
])

/**
 * The zoned-conversion boundary (D-046): `WeekGrid` itself works purely in
 * "fake local" `Date`s whose local getters already hold the viewer's
 * profile-zone wall clock. Everything that crosses the wire is converted
 * here, using `Intl.DateTimeFormat` (no timezone library is in the write
 * set) rather than the browser's own zone.
 */
function zonedOffsetMinutes(utcMs: number, timeZone: string): number {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
  const parts = Object.fromEntries(
    formatter.formatToParts(new Date(utcMs)).map((part) => [part.type, part.value]),
  )
  const hour = parts.hour === '24' ? 0 : Number(parts.hour)
  const asUtc = Date.UTC(
    Number(parts.year),
    Number(parts.month) - 1,
    Number(parts.day),
    hour,
    Number(parts.minute),
    Number(parts.second),
  )
  return (asUtc - utcMs) / 60_000
}

function instantToFakeLocal(iso: string, timeZone: string): Date {
  const utcMs = new Date(iso).getTime()
  const offset = zonedOffsetMinutes(utcMs, timeZone)
  return new Date(utcMs + offset * 60_000)
}

function fakeLocalToInstant(fakeLocal: Date, timeZone: string): string {
  const guessUtc = Date.UTC(
    fakeLocal.getUTCFullYear(),
    fakeLocal.getUTCMonth(),
    fakeLocal.getUTCDate(),
    fakeLocal.getUTCHours(),
    fakeLocal.getUTCMinutes(),
    0,
    0,
  )
  const offset = zonedOffsetMinutes(guessUtc, timeZone)
  const instantMs = guessUtc - offset * 60_000
  const correctedOffset = zonedOffsetMinutes(instantMs, timeZone)
  return new Date(guessUtc - correctedOffset * 60_000).toISOString()
}

function datetimeLocalValue(fakeLocal: Date): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${fakeLocal.getUTCFullYear()}-${pad(fakeLocal.getUTCMonth() + 1)}-${pad(
    fakeLocal.getUTCDate(),
  )}T${pad(fakeLocal.getUTCHours())}:${pad(fakeLocal.getUTCMinutes())}`
}

type CalendarOccurrence = Awaited<
  ReturnType<typeof getCalendarView>
>['myAvailability']['occurrences'][number]

/** The grid addresses an occurrence by a single string; a one-time block is
 * its own id, a series occurrence is the (series, date) pair that identifies
 * it on the wire. */
function occurrenceKey(occurrence: CalendarOccurrence): string {
  return occurrence.origin.kind === 'block'
    ? (occurrence.origin.blockId ?? '')
    : `${occurrence.origin.seriesId}:${occurrence.origin.occurrenceDate}`
}

function parseDatetimeLocal(value: string): Date {
  const [datePart, timePart] = value.split('T')
  const [year, month, day] = datePart.split('-').map(Number)
  const [hour, minute] = (timePart ?? '00:00').split(':').map(Number)
  return new Date(Date.UTC(year, month - 1, day, hour, minute, 0, 0))
}

function weekStartsOnIso(locale: string): number {
  try {
    const withWeekInfo = new Intl.Locale(locale) as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number }
      weekInfo?: { firstDay: number }
    }
    const info = withWeekInfo.getWeekInfo?.() ?? withWeekInfo.weekInfo
    if (info?.firstDay) return info.firstDay
  } catch {
    // Unsupported locale/environment — the ISO default below still holds.
  }
  return 1
}

function startOfLocaleWeek(reference: Date, firstDayIso: number): Date {
  const jsFirstDay = firstDayIso % 7 // ISO 7 (Sunday) -> JS 0
  const diff = (reference.getUTCDay() - jsFirstDay + 7) % 7
  const start = new Date(reference)
  start.setUTCDate(start.getUTCDate() - diff)
  start.setUTCHours(0, 0, 0, 0)
  return start
}

type EditingTarget =
  | { kind: 'block'; blockId: string }
  | { kind: 'occurrence'; seriesId: string; occurrenceDate: string }

/** What the undo toast would delete, and the message announcing it. */
type UndoTarget = { kind: 'block' | 'series'; id: string; message: string }

export function WeekGridPage() {
  const { t, i18n } = useTranslation(['availability', 'errors'])
  const location = useLocation()
  const queryClient = useQueryClient()
  const [weekOffset, setWeekOffset] = useState(0)
  // `null` means "no explicit paging yet" — the window is then derived from
  // today (§9 S5's initial-window rule), which is also what the Today button
  // and the week navigation restore.
  const [dayWindowStart, setDayWindowStart] = useState<number | null>(null)
  const [editingTarget, setEditingTarget] = useState<EditingTarget | null>(null)
  const [pendingScope, setPendingScope] = useState<OccurrenceScope>('onlyThis')
  const [scopeDialogOpen, setScopeDialogOpen] = useState(false)
  const [blockDialogOpen, setBlockDialogOpen] = useState(false)
  const [blockEditorValue, setBlockEditorValue] = useState<BlockEditorValue>({
    start: '',
    end: '',
  })
  const [pendingRange, setPendingRange] = useState<GridRange | null>(null)
  const [pendingAnchor, setPendingAnchor] = useState<DOMRect | null>(null)
  const [undoTarget, setUndoTarget] = useState<UndoTarget | null>(null)
  const [layers, setLayers] = useState<CalendarLayerState | null>(null)
  const [showEarlyHours, setShowEarlyHours] = useState(false)
  const [earlyHoursMessage, setEarlyHoursMessage] = useState('')
  // CALENDAR_DESIGN.md §9 S4: below `lg` the summary starts collapsed behind
  // a disclosure; at `lg`+ it is always visible in the sticky aside and this
  // state has no effect (the aside ignores it).
  const [summaryOpen, setSummaryOpen] = useState(false)
  const editTrigger = useRef<HTMLElement | null>(null)
  // `CommitBlockDialog` conditional-mounts per open (`{pendingRange && ...}`),
  // so Radix has no stable trigger element to restore focus to when it
  // unmounts — unlike the always-mounted edit dialogs below, which hand Radix
  // `editTrigger` via `onCloseAutoFocus`. This ref/effect pair is the
  // equivalent restore for the commit dialog: the roving-tabindex cell in the
  // grid, re-focused once the dialog's own unmount has finished painting.
  const gridContainerRef = useRef<HTMLDivElement>(null)
  const wasCommitDialogOpen = useRef(false)
  useEffect(() => {
    const isOpen = pendingRange !== null && pendingAnchor !== null
    const closedJustNow = wasCommitDialogOpen.current && !isOpen
    wasCommitDialogOpen.current = isOpen
    if (!closedJustNow) return
    const raf = requestAnimationFrame(() => {
      gridContainerRef.current
        ?.querySelector<HTMLElement>('[role="gridcell"][tabindex="0"]')
        ?.focus()
    })
    return () => cancelAnimationFrame(raf)
  }, [pendingRange, pendingAnchor])

  const isAgenda = location.pathname.endsWith('/agenda')

  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const timeZone = profile.data?.timeZone ?? 'UTC'

  // Captured once per mount: `weekStart` must keep a stable identity across
  // renders that don't change weekOffset/timeZone/language, since WeekGrid's
  // bandMeta memo is keyed on that identity (§8) — a fresh `new Date()` per
  // render would defeat it on every render regardless of the deps below.
  const [mountInstant] = useState(() => new Date().toISOString())
  const weekStart = useMemo(() => {
    const zonedNow = instantToFakeLocal(mountInstant, timeZone)
    const anchor = new Date(zonedNow.getTime() + weekOffset * 7 * DAY_MS)
    return startOfLocaleWeek(anchor, weekStartsOnIso(i18n.language))
  }, [mountInstant, timeZone, weekOffset, i18n.language])
  // The now-line's clock (§9 S2): `WeekGrid` is zone-free, so the conversion
  // to the viewer's profile zone happens here, at the same `instantToFakeLocal`
  // boundary every other grid Date crosses, and re-derives on a minute tick.
  const [now, setNow] = useState<Date | null>(null)
  useEffect(() => {
    const tick = () => setNow(instantToFakeLocal(new Date().toISOString(), timeZone))
    tick()
    const interval = setInterval(tick, 60_000)
    return () => clearInterval(interval)
  }, [timeZone])

  const weekEnd = new Date(weekStart.getTime() + 7 * DAY_MS)
  const from = fakeLocalToInstant(weekStart, timeZone)
  const to = fakeLocalToInstant(weekEnd, timeZone)

  const calendar = useQuery({
    queryKey: availabilityKeys.calendar(from, to),
    queryFn: () => getCalendarView(from, to),
    enabled: Boolean(profile.data),
  })

  const invalidate = () => invalidateAvailabilityQueries(queryClient)

  const createBlock = useMutation({
    mutationFn: createAvailabilityBlock,
    onSuccess: (created) => {
      setUndoTarget({
        kind: 'block',
        id: created.blockId,
        message: t('commit.savedOnce'),
      })
      return invalidate()
    },
  })
  const createSeries = useMutation({
    mutationFn: createAvailabilitySeries,
    onSuccess: (created) => {
      setUndoTarget({
        kind: 'series',
        id: created.seriesId,
        message: t('commit.savedWeekly'),
      })
      return invalidate()
    },
  })
  // The editor closes only once the calendar query has actually refetched.
  // `BlockEditorDialog` restores focus to the cell that opened it, and an edit
  // repaints the block onto different cells — closing on the mutation's own
  // response would hand a keyboard user focus on a cell whose content is still
  // about to change, so `Enter` there starts painting a new selection instead
  // of reopening the block (WCAG 3.2.2). `createBlock` above already awaits its
  // invalidation for the same reason.
  const editBlock = useMutation({
    mutationFn: ({ blockId, body }: { blockId: string; body: { startTime: string; endTime: string } }) =>
      editAvailabilityBlock(blockId, body),
    onSuccess: async () => {
      await invalidate()
      setBlockDialogOpen(false)
    },
  })
  const deleteBlock = useMutation({
    mutationFn: deleteAvailabilityBlock,
    onSuccess: async () => {
      await invalidate()
      setBlockDialogOpen(false)
    },
  })
  const deleteSeries = useMutation({
    mutationFn: deleteAvailabilitySeries,
    onSuccess: invalidate,
  })
  const editOccurrence = useMutation({
    mutationFn: ({
      seriesId,
      occurrenceDate,
      body,
    }: {
      seriesId: string
      occurrenceDate: string
      body: Parameters<typeof editAvailabilityOccurrence>[2]
    }) => editAvailabilityOccurrence(seriesId, occurrenceDate, body),
    onSuccess: async () => {
      await invalidate()
      setBlockDialogOpen(false)
    },
  })
  const deleteOccurrence = useMutation({
    mutationFn: ({ seriesId, occurrenceDate }: { seriesId: string; occurrenceDate: string }) =>
      deleteAvailabilityOccurrence(seriesId, occurrenceDate),
    onSuccess: async () => {
      await invalidate()
      setBlockDialogOpen(false)
    },
  })

  // D-111: every layer is a toggle over the same one `GET /calendar`
  // response — never a second fetch. `layers` starts `null` until
  // `CalendarLayers` reports its (component-state, D-135) defaults, so
  // nothing is hidden before that first report lands.
  const showMyAvailability = layers?.myAvailability ?? true
  const showSessions = layers?.sessions ?? true
  const showPollOptions = layers?.pollOptions ?? true

  // Memoized so their reference identity is stable across renders that
  // don't actually change the underlying data — `WeekGrid`'s band-metadata
  // memo is keyed on these identities (§8), so an unstable array here would
  // defeat that memo on every render.
  const myOccurrences = calendar.data?.myAvailability.occurrences
  const occurrences: GridOccurrence[] = useMemo(
    () =>
      showMyAvailability
        ? (myOccurrences ?? []).map((occurrence) => ({
            id: occurrenceKey(occurrence),
            start: instantToFakeLocal(occurrence.startTime, timeZone),
            end: instantToFakeLocal(occurrence.endTime, timeZone),
          }))
        : [],
    [showMyAvailability, myOccurrences, timeZone],
  )
  const myMerged = calendar.data?.myAvailability.merged
  const merged: GridRange[] = useMemo(
    () =>
      showMyAvailability
        ? (myMerged ?? []).map((range) => ({
            start: instantToFakeLocal(range.start, timeZone),
            end: instantToFakeLocal(range.end, timeZone),
          }))
        : [],
    [showMyAvailability, myMerged, timeZone],
  )

  // The rules list is not a layer: it manages what the viewer published, so it
  // stays readable while the grid layer is toggled off (D-135 keeps the toggle
  // presentational).
  const oneOffBlocks: AvailabilityOneOff[] = (
    calendar.data?.myAvailability.occurrences ?? []
  ).flatMap((occurrence) =>
    occurrence.origin.kind === 'block' && occurrence.origin.blockId
      ? [
          {
            blockId: occurrence.origin.blockId,
            startTime: occurrence.startTime,
            endTime: occurrence.endTime,
          },
        ]
      : [],
  )
  const availabilitySeries = calendar.data?.myAvailability.series ?? []
  // The mobile disclosure's collapsed summary line (§9 S4) — every published
  // rule, weekly or one-off, same as `AvailabilitySummary` itself lists.
  const availabilityWindowCount = availabilitySeries.length + oneOffBlocks.length

  // Stabilized by reference (react-query keeps `data` referentially equal
  // across renders that don't refetch) so `CalendarLayers`' state-report
  // effect, which depends on this array, doesn't re-fire every render and
  // loop with `setLayers` here.
  const friendAvailabilityData = calendar.data?.friendAvailability
  const friends = useMemo(
    () => (friendAvailabilityData ?? []).map((entry) => entry.owner),
    [friendAvailabilityData],
  )

  // A-080/D-117: friendAvailability carries ranges and a masked owner only
  // (no id ever reaches the client) — the per-friend filter therefore keys
  // on `ordinal`, which is only stable within this one response.
  const showFriendAvailability = layers?.friendAvailability ?? true
  const shownFriendOrdinals = layers?.shownFriendOrdinals ?? null
  const friendRanges: GridFriendRange[] = useMemo(
    () =>
      showFriendAvailability
        ? (friendAvailabilityData ?? []).flatMap((entry) => {
            if (shownFriendOrdinals && !shownFriendOrdinals.has(Number(entry.owner.ordinal))) {
              return []
            }
            const ownerLabel = entry.owner.displayName ?? t('calendar.anonymousFriend')
            return entry.ranges.map((range) => ({
              ownerLabel,
              start: instantToFakeLocal(range.start, timeZone),
              end: instantToFakeLocal(range.end, timeZone),
            }))
          })
        : [],
    [showFriendAvailability, friendAvailabilityData, shownFriendOrdinals, timeZone, t],
  )

  // A-077 at access-kind `any`: the sessions layer already includes
  // inherited/discoverable rows (D-149), each carrying `viewerRelation` so
  // the grid can style them distinctly — never a second fetch or filter.
  const sessionsData = calendar.data?.sessions
  const sessionEntries: GridSessionEntry[] = useMemo(
    () =>
      showSessions
        ? (sessionsData ?? []).map((session) => ({
            id: session.id,
            title: session.title,
            start: instantToFakeLocal(session.startTime, timeZone),
            end: instantToFakeLocal(
              new Date(
                new Date(session.startTime).getTime() + Number(session.durationMinutes) * 60_000,
              ).toISOString(),
              timeZone,
            ),
            viewerRelation: session.viewerRelation as GridViewerRelation,
          }))
        : [],
    [showSessions, sessionsData, timeZone],
  )

  // D-111/A-080 pollOptions layer (design §5, Phase 6 S4c fill): one instant per option, no
  // duration (spec §7.3-1) — start and end are the same fake-local instant, painted as a point.
  const pollOptionsData = calendar.data?.pollOptions
  const pollOptionEntries: GridPollOptionEntry[] = useMemo(
    () =>
      showPollOptions
        ? (pollOptionsData ?? []).flatMap((poll) =>
            poll.options.map((option) => ({
              id: option.optionId,
              pollId: poll.pollId,
              pollTitle: poll.title,
              start: instantToFakeLocal(option.startTime, timeZone),
              ownVote: (option.ownVote as GridPollOptionEntry['ownVote']) ?? null,
            })),
          )
        : [],
    [showPollOptions, pollOptionsData, timeZone],
  )

  const agendaEntries: AgendaEntry[] = [
    ...(showMyAvailability
      ? (calendar.data?.myAvailability.occurrences ?? []).map((occurrence, index) => ({
          id: `availability-${index}`,
          kind: 'availability' as const,
          start: instantToFakeLocal(occurrence.startTime, timeZone),
          end: instantToFakeLocal(occurrence.endTime, timeZone),
          title: t('calendar.layers.myAvailability'),
        }))
      : []),
    ...sessionEntries.map((session) => ({
      id: `session-${session.id}`,
      kind: 'session' as const,
      start: session.start,
      end: session.end,
      title:
        session.viewerRelation === 'inherited'
          ? `${session.title} (${t('a11y.inherited')})`
          : session.title,
    })),
    ...pollOptionEntries.map((option) => ({
      id: `poll-option-${option.id}`,
      kind: 'pollOption' as const,
      start: option.start,
      end: option.start,
      title: option.pollTitle,
    })),
  ]

  // `timeZone: 'UTC'` here is not a bug: `weekStart`/grid dates hold the
  // profile-zone wall clock on their UTC accessors (D-046), so formatting
  // must read them back the same way — never through the host's own zone.
  const dayLabels = Array.from({ length: 7 }, (_, index) => {
    const day = new Date(weekStart.getTime() + index * DAY_MS)
    return new Intl.DateTimeFormat(i18n.language, {
      weekday: 'short',
      timeZone: 'UTC',
    }).format(day)
  })
  const timeLabelFormatter = (date: Date) =>
    new Intl.DateTimeFormat(i18n.language, {
      hour: 'numeric',
      minute: '2-digit',
      timeZone: 'UTC',
    }).format(date)
  const dateLabelFormatter = (date: Date) =>
    new Intl.DateTimeFormat(i18n.language, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    }).format(date)

  const startHour = showEarlyHours ? 0 : WAKING_START_HOUR

  // §9 S5's day window. `todayIndex` is a pure difference between the injected
  // fake-local `now` and this week's own start — never the host clock — so it
  // is `null` on every week that does not contain today.
  const isMobile = useIsMobileViewport()
  const dayWindowLength = isMobile ? MOBILE_DAY_WINDOW_LENGTH : DAYS_PER_WEEK
  const maxDayWindowStart = DAYS_PER_WEEK - dayWindowLength
  const todayIndex = useMemo(() => {
    if (!now) return null
    const index = Math.floor((now.getTime() - weekStart.getTime()) / DAY_MS)
    return index >= 0 && index < DAYS_PER_WEEK ? index : null
  }, [now, weekStart])
  const defaultDayWindowStart =
    todayIndex === null ? 0 : clamp(todayIndex, 0, maxDayWindowStart)
  const dayWindowStartValue = clamp(
    dayWindowStart ?? defaultDayWindowStart,
    0,
    maxDayWindowStart,
  )
  const dayWindowDays = Array.from(
    { length: dayWindowLength },
    (_, index) => dayWindowStartValue + index,
  )

  function goToWeek(delta: number) {
    setWeekOffset((value) => value + delta)
    setDayWindowStart(null)
  }

  // Prev/next step the window by a day and roll into the neighbouring week at
  // the week's edges, landing the window at the far end so the step still
  // reads as one day forward or back (§9 S5).
  function stepDayWindow(delta: -1 | 1) {
    const next = dayWindowStartValue + delta
    if (next < 0) {
      setWeekOffset((value) => value - 1)
      setDayWindowStart(maxDayWindowStart)
      return
    }
    if (next > maxDayWindowStart) {
      setWeekOffset((value) => value + 1)
      setDayWindowStart(0)
      return
    }
    setDayWindowStart(next)
  }

  function goToToday() {
    setWeekOffset(0)
    setDayWindowStart(null)
  }

  function toggleEarlyHours() {
    const next = !showEarlyHours
    setShowEarlyHours(next)
    setEarlyHoursMessage(
      next ? t('viewport.earlyHoursShown') : t('viewport.earlyHoursHidden'),
    )
  }

  // D-009/§7: a painted range is a proposal — the commit step asks whether it
  // is one-time or weekly before anything reaches the API.
  function handleCreateBlock(range: GridRange, anchor: DOMRect) {
    setPendingRange(range)
    setPendingAnchor(anchor)
  }

  function handleCommit(recurrence: CommitRecurrence) {
    const range = pendingRange
    if (!range) return
    setPendingRange(null)
    setPendingAnchor(null)

    if (recurrence === 'once') {
      createBlock.mutate({
        startTime: fakeLocalToInstant(range.start, timeZone),
        endTime: fakeLocalToInstant(range.end, timeZone),
      })
      return
    }

    // Fake-local (D-046): the UTC accessors already read the viewer's own
    // wall clock, so the weekday and time of day are taken straight off them.
    const pad = (value: number) => String(value).padStart(2, '0')
    createSeries.mutate({
      dayOfWeek: ((range.start.getUTCDay() + 6) % 7) + 1, // ISO 1..7
      startTime: `${pad(range.start.getUTCHours())}:${pad(range.start.getUTCMinutes())}`,
      durationMinutes: Math.round((range.end.getTime() - range.start.getTime()) / 60_000),
      effectiveFrom: datetimeLocalValue(range.start).slice(0, 10),
      effectiveUntil: null,
    })
  }

  function handleUndo() {
    const target = undoTarget
    setUndoTarget(null)
    if (!target) return
    if (target.kind === 'series') {
      deleteSeries.mutate(target.id)
    } else {
      deleteBlock.mutate(target.id)
    }
  }

  function findOccurrence(occurrenceId: string): CalendarOccurrence | undefined {
    return (calendar.data?.myAvailability.occurrences ?? []).find(
      (occurrence) => occurrenceKey(occurrence) === occurrenceId,
    )
  }

  function handleEditOccurrence(occurrenceId: string) {
    editTrigger.current = document.activeElement as HTMLElement | null
    const raw = findOccurrence(occurrenceId)
    if (!raw) return

    setBlockEditorValue({
      start: datetimeLocalValue(instantToFakeLocal(raw.startTime, timeZone)),
      end: datetimeLocalValue(instantToFakeLocal(raw.endTime, timeZone)),
    })

    if (raw.origin.kind === 'occurrence' && raw.origin.seriesId && raw.origin.occurrenceDate) {
      setEditingTarget({
        kind: 'occurrence',
        seriesId: raw.origin.seriesId,
        occurrenceDate: raw.origin.occurrenceDate,
      })
      setPendingScope('onlyThis')
      setScopeDialogOpen(true)
    } else if (raw.origin.blockId) {
      setEditingTarget({ kind: 'block', blockId: raw.origin.blockId })
      setBlockDialogOpen(true)
    }
  }

  // A dragged edge is a proposed range, not a committed one. For a recurring
  // occurrence it goes through the scope dialog first: without that, nudging an
  // edge could silently rewrite the whole series (D-025 keeps "only this
  // occurrence" the default). A one-time block has no such ambiguity, so it
  // saves straight away.
  function handleAdjustOccurrence(adjustment: GridAdjustment) {
    editTrigger.current = document.activeElement as HTMLElement | null
    const raw = findOccurrence(adjustment.occurrenceId)
    if (!raw) return

    setBlockEditorValue({
      start: datetimeLocalValue(adjustment.start),
      end: datetimeLocalValue(adjustment.end),
    })

    if (raw.origin.kind === 'occurrence' && raw.origin.seriesId && raw.origin.occurrenceDate) {
      setEditingTarget({
        kind: 'occurrence',
        seriesId: raw.origin.seriesId,
        occurrenceDate: raw.origin.occurrenceDate,
      })
      setPendingScope('onlyThis')
      setScopeDialogOpen(true)
      return
    }

    if (raw.origin.blockId) {
      setEditingTarget({ kind: 'block', blockId: raw.origin.blockId })
      editBlock.mutate({
        blockId: raw.origin.blockId,
        body: {
          startTime: fakeLocalToInstant(adjustment.start, timeZone),
          endTime: fakeLocalToInstant(adjustment.end, timeZone),
        },
      })
    }
  }

  function handleScopeConfirm(scope: OccurrenceScope) {
    setPendingScope(scope)
    setScopeDialogOpen(false)
    setBlockDialogOpen(true)
  }

  function handleBlockSave(value: BlockEditorValue) {
    if (!editingTarget) return

    if (editingTarget.kind === 'block') {
      editBlock.mutate({
        blockId: editingTarget.blockId,
        body: {
          startTime: fakeLocalToInstant(parseDatetimeLocal(value.start), timeZone),
          endTime: fakeLocalToInstant(parseDatetimeLocal(value.end), timeZone),
        },
      })
      return
    }

    if (pendingScope === 'thisAndFuture') {
      const startLocal = parseDatetimeLocal(value.start)
      const endLocal = parseDatetimeLocal(value.end)
      const dayOfWeek = ((startLocal.getUTCDay() + 6) % 7) + 1
      const durationMinutes = Math.round(
        (endLocal.getTime() - startLocal.getTime()) / 60_000,
      )
      const pad = (n: number) => String(n).padStart(2, '0')
      editOccurrence.mutate({
        seriesId: editingTarget.seriesId,
        occurrenceDate: editingTarget.occurrenceDate,
        body: {
          scope: 'thisAndFuture',
          dayOfWeek,
          // "HH:mm" — the wire's `LocalTimePattern` is strict, no seconds
          // (`AvailabilityWireFormat.cs`'s `TimePattern`); a trailing ":ss"
          // fails server-side parsing (`availability_validation`, caught by
          // the S6d browser lane, not by the mocked Vitest path).
          startTime: `${pad(startLocal.getUTCHours())}:${pad(startLocal.getUTCMinutes())}`,
          durationMinutes,
          endTime: null,
        },
      })
      return
    }

    editOccurrence.mutate({
      seriesId: editingTarget.seriesId,
      occurrenceDate: editingTarget.occurrenceDate,
      body: {
        scope: 'onlyThis',
        startTime: fakeLocalToInstant(parseDatetimeLocal(value.start), timeZone),
        endTime: fakeLocalToInstant(parseDatetimeLocal(value.end), timeZone),
        dayOfWeek: null,
        durationMinutes: null,
      },
    })
  }

  // One in-flight delete at a time is all the summary can start, so the id of
  // whichever mutation is running names the row to disable.
  const deletingId: string | null = deleteSeries.isPending
    ? deleteSeries.variables
    : deleteBlock.isPending
      ? deleteBlock.variables
      : null

  const activeError =
    calendar.error ??
    createBlock.error ??
    createSeries.error ??
    editBlock.error ??
    deleteBlock.error ??
    deleteSeries.error ??
    editOccurrence.error ??
    deleteOccurrence.error

  function errorMessage(error: unknown): string {
    if (!isApiError(error)) return t('generic', { ns: 'errors' })
    if (AVAILABILITY_PROBLEM_CODES.has(error.code)) {
      return t(`errors.codes.${error.code}`, { ns: 'availability' })
    }
    return t(error.code, {
      ns: 'errors',
      defaultValue: t('generic', { ns: 'errors' }),
    })
  }

  function handleDelete() {
    if (!editingTarget) return
    if (editingTarget.kind === 'block') {
      deleteBlock.mutate(editingTarget.blockId)
    } else {
      deleteOccurrence.mutate({
        seriesId: editingTarget.seriesId,
        occurrenceDate: editingTarget.occurrenceDate,
      })
    }
  }

  // CALENDAR_DESIGN.md §9 S4: one aside tree, reused by both the week-view
  // and agenda branches below — the agenda route keeps layer control (per
  // D-111/D-135) and now gains the summary alongside it, at parity with the
  // week view rather than as a special case.
  const aside = (
    <aside className="flex flex-col gap-4 lg:sticky lg:top-4">
      <button
        aria-controls="availability-summary-mobile-panel"
        aria-expanded={summaryOpen}
        className="flex w-full items-center justify-between gap-2 rounded-card border border-ink/25 bg-card px-3 py-2 text-left lg:hidden"
        type="button"
        onClick={() => setSummaryOpen((open) => !open)}
      >
        <span className="min-w-0">
          <span className="block font-display font-semibold text-ink">
            {t('summary.heading')}
          </span>
          {!summaryOpen && (
            <span className="block truncate text-meta text-ink-soft">
              {t('summary.windowsCount', { count: availabilityWindowCount })}
            </span>
          )}
          <span className="sr-only">{t(summaryOpen ? 'summary.collapse' : 'summary.expand')}</span>
        </span>
        <svg
          aria-hidden="true"
          className={`size-5 shrink-0 transition-transform ${summaryOpen ? 'rotate-180' : ''}`}
          fill="none"
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          viewBox="0 0 24 24"
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      {summaryOpen && (
        <div className="lg:hidden" id="availability-summary-mobile-panel">
          <AvailabilitySummary
            deletingId={deletingId}
            oneOffs={oneOffBlocks}
            series={availabilitySeries}
            timeZone={timeZone}
            titleId="availability-summary-heading-mobile"
            onDeleteBlock={(blockId) => deleteBlock.mutate(blockId)}
            onDeleteSeries={(seriesId) => deleteSeries.mutate(seriesId)}
          />
        </div>
      )}
      <div className="hidden lg:block">
        <AvailabilitySummary
          deletingId={deletingId}
          oneOffs={oneOffBlocks}
          series={availabilitySeries}
          timeZone={timeZone}
          titleId="availability-summary-heading-desktop"
          onDeleteBlock={(blockId) => deleteBlock.mutate(blockId)}
          onDeleteSeries={(seriesId) => deleteSeries.mutate(seriesId)}
        />
      </div>
      <CalendarLayers
        friends={friends}
        pollItems={(calendar.data?.pollOptions ?? []).map((poll) => ({
          pollId: poll.pollId,
          title: poll.title,
        }))}
        onChange={setLayers}
      />
    </aside>
  )

  return (
    <main data-testid="screen-calendar">
      <PageScaffold>
        <PageHeader title={t('calendar.heading')} />

        {activeError && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage(activeError)}
          </StatusBanner>
        )}

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between">
          <div className={segmentStripClasses} role="tablist">
            <Link
              aria-selected={!isAgenda}
              className={segmentItemClasses(!isAgenda)}
              role="tab"
              to="/calendar"
            >
              {t('calendar.week')}
            </Link>
            <Link
              aria-selected={isAgenda}
              className={segmentItemClasses(isAgenda)}
              role="tab"
              to="/calendar/agenda"
            >
              {t('calendar.agenda')}
            </Link>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="neutral" onClick={() => goToWeek(-1)}>
              {t('calendar.previousWeek')}
            </Button>
            <Button type="button" variant="neutral" onClick={() => goToWeek(1)}>
              {t('calendar.nextWeek')}
            </Button>
          </div>
        </div>

        {/* CALENDAR_DESIGN.md §9 S4: the sticky aside hosts the summary then
            the legend for BOTH view branches below (agenda keeps layer
            control per D-111/D-135) — a `lg`+ two-column grid, single-column
            with the summary behind a mobile disclosure below `lg`. */}
        {isAgenda ? (
          <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)] lg:items-start">
            {aside}
            <div className="min-w-0">
              <AgendaView
                dateLabelFormatter={dateLabelFormatter}
                entries={agendaEntries}
                timeLabelFormatter={timeLabelFormatter}
              />
            </div>
          </div>
        ) : (
          <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)] lg:items-start">
            {aside}
            <div ref={gridContainerRef} className="min-w-0 space-y-2">
              {/* Stays mounted in both states: unmounting the control on
                  activation would drop focus to document.body and leave the 32
                  revealed rows unannounced (WCAG 2.4.3). */}
              <Button type="button" variant="neutral" onClick={toggleEarlyHours}>
                {showEarlyHours
                  ? t('viewport.hideEarlyHours')
                  : t('viewport.showEarlyHours')}
              </Button>
              <p
                aria-live="polite"
                className="sr-only"
                data-testid="early-hours-status"
                role="status"
              >
                {earlyHoursMessage}
              </p>
              {/* §9 S5: the day pager exists only where the window applies —
                  at `sm`+ the grid is the full week and there is nothing to
                  page. The strip's seven buttons are the displayed week's
                  days, so a jump never changes which week is shown. */}
              {isMobile && (
                <div className="space-y-1.5 sm:hidden" data-testid="day-pager">
                  <div
                    aria-label={t('calendar.dayPager.label')}
                    className="grid grid-cols-7 gap-1"
                    role="group"
                  >
                    {dayLabels.map((label, index) => {
                      const inWindow = dayWindowDays.includes(index)
                      return (
                        <button
                          key={label}
                          aria-current={index === todayIndex ? 'date' : undefined}
                          aria-label={t(
                            index === todayIndex
                              ? 'calendar.dayPager.showDayToday'
                              : 'calendar.dayPager.showDay',
                            { day: label },
                          )}
                          aria-pressed={inWindow}
                          className={`min-h-11 rounded-xl border font-display text-[12px] font-bold ${
                            inWindow
                              ? 'border-primary bg-primary/20 text-ink'
                              : 'border-ink/25 bg-card text-ink-soft'
                          } ${index === todayIndex ? 'underline decoration-2 underline-offset-4' : ''}`}
                          type="button"
                          onClick={() => setDayWindowStart(clamp(index, 0, maxDayWindowStart))}
                        >
                          {label}
                        </button>
                      )
                    })}
                  </div>
                  <div className="flex items-center gap-1.5">
                    <Button
                      aria-label={t('calendar.dayPager.previousDay')}
                      type="button"
                      variant="neutral"
                      onClick={() => stepDayWindow(-1)}
                    >
                      &lsaquo;
                    </Button>
                    <p className="min-w-0 flex-1 truncate text-center font-display text-[12px] font-bold text-ink-soft">
                      {t('calendar.dayPager.range', {
                        start: dayLabels[dayWindowDays[0]],
                        end: dayLabels[dayWindowDays[dayWindowDays.length - 1]],
                      })}
                    </p>
                    <Button type="button" variant="neutral" onClick={goToToday}>
                      {t('calendar.dayPager.today')}
                    </Button>
                    <Button
                      aria-label={t('calendar.dayPager.nextDay')}
                      type="button"
                      variant="neutral"
                      onClick={() => stepDayWindow(1)}
                    >
                      &rsaquo;
                    </Button>
                  </div>
                </div>
              )}
              <WeekGrid
                dayLabels={dayLabels}
                dayWindow={
                  isMobile
                    ? { start: dayWindowStartValue, length: dayWindowLength }
                    : undefined
                }
                endHour={END_HOUR}
                friendRanges={friendRanges}
                merged={merged}
                now={now}
                occurrences={occurrences}
                pollOptionEntries={pollOptionEntries}
                sessionEntries={sessionEntries}
                startHour={startHour}
                timeLabelFormatter={timeLabelFormatter}
                weekStart={weekStart}
                onAdjustOccurrence={handleAdjustOccurrence}
                onCreateBlock={handleCreateBlock}
                onDayWindowStartChange={setDayWindowStart}
                onEditOccurrence={handleEditOccurrence}
              />
            </div>
          </div>
        )}
      </PageScaffold>

      {pendingRange && pendingAnchor && (
        <CommitBlockDialog
          anchor={pendingAnchor}
          pending={createBlock.isPending || createSeries.isPending}
          range={pendingRange}
          onCancel={() => {
            setPendingRange(null)
            setPendingAnchor(null)
          }}
          onConfirm={handleCommit}
        />
      )}

      <Toast.Provider label={t('commit.notificationLabel')} swipeDirection="right">
        {/* The toast carries the only Undo affordance and is reached by the F8
            hotkey, so it must not expire on a timer (WCAG 2.2.1). */}
        <Toast.Root
          className="!rounded-card !border !border-ink/25 !shadow-[var(--shadow-pop)]"
          duration={Infinity}
          open={undoTarget !== null}
          onOpenChange={(open) => {
            if (!open) setUndoTarget(null)
          }}
        >
          <Toast.Description>{undoTarget?.message}</Toast.Description>
          <div className="mt-2 flex gap-2">
            <Toast.Action
              altText={t('commit.undo')}
              className={`inline-flex min-h-11 items-center px-3 text-ink bg-card ${raisedInteractiveClasses}`}
              onClick={handleUndo}
            >
              {t('commit.undo')}
            </Toast.Action>
            <Toast.Close
              className={`inline-flex min-h-11 items-center px-3 text-ink bg-card ${raisedInteractiveClasses}`}
            >
              {t('commit.dismiss')}
            </Toast.Close>
          </div>
        </Toast.Root>
        <Toast.Viewport label={t('commit.notificationsRegion')} />
      </Toast.Provider>

      <OccurrenceScopeDialog
        open={scopeDialogOpen}
        triggerRef={editTrigger}
        onConfirm={handleScopeConfirm}
        onOpenChange={setScopeDialogOpen}
      />

      <BlockEditorDialog
        initialEnd={blockEditorValue.end}
        initialStart={blockEditorValue.start}
        open={blockDialogOpen}
        pending={
          editBlock.isPending ||
          deleteBlock.isPending ||
          editOccurrence.isPending ||
          deleteOccurrence.isPending
        }
        triggerRef={editTrigger}
        onDelete={handleDelete}
        onOpenChange={setBlockDialogOpen}
        onSave={handleBlockSave}
      />
    </main>
  )
}
