import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router'
import { isApiError } from '../../api/client'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import { AgendaView, type AgendaEntry } from './AgendaView'
import { BlockEditorDialog, type BlockEditorValue } from './BlockEditorDialog'
import { CalendarLayers, type CalendarLayerState } from './CalendarLayers'
import {
  OccurrenceScopeDialog,
  type OccurrenceScope,
} from './OccurrenceScopeDialog'
import {
  WeekGrid,
  type GridFriendRange,
  type GridOccurrence,
  type GridRange,
  type GridSessionEntry,
  type GridViewerRelation,
} from './WeekGrid'
import {
  availabilityKeys,
  createAvailabilityBlock,
  deleteAvailabilityBlock,
  deleteAvailabilityOccurrence,
  editAvailabilityBlock,
  editAvailabilityOccurrence,
  getCalendarView,
  invalidateAvailabilityQueries,
} from './availabilityQueries'

const DAY_MS = 24 * 60 * 60 * 1000

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

export function WeekGridPage() {
  const { t, i18n } = useTranslation(['availability', 'errors'])
  const location = useLocation()
  const queryClient = useQueryClient()
  const [weekOffset, setWeekOffset] = useState(0)
  const [editingTarget, setEditingTarget] = useState<EditingTarget | null>(null)
  const [pendingScope, setPendingScope] = useState<OccurrenceScope>('onlyThis')
  const [scopeDialogOpen, setScopeDialogOpen] = useState(false)
  const [blockDialogOpen, setBlockDialogOpen] = useState(false)
  const [blockEditorValue, setBlockEditorValue] = useState<BlockEditorValue>({
    start: '',
    end: '',
  })
  const [layers, setLayers] = useState<CalendarLayerState | null>(null)
  const editTrigger = useRef<HTMLElement | null>(null)

  const isAgenda = location.pathname.endsWith('/agenda')

  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const timeZone = profile.data?.timeZone ?? 'UTC'

  const zonedNow = instantToFakeLocal(new Date().toISOString(), timeZone)
  const anchor = new Date(zonedNow.getTime() + weekOffset * 7 * DAY_MS)
  const weekStart = startOfLocaleWeek(anchor, weekStartsOnIso(i18n.language))
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
    onSuccess: invalidate,
  })
  const editBlock = useMutation({
    mutationFn: ({ blockId, body }: { blockId: string; body: { startTime: string; endTime: string } }) =>
      editAvailabilityBlock(blockId, body),
    onSuccess: () => {
      invalidate()
      setBlockDialogOpen(false)
    },
  })
  const deleteBlock = useMutation({
    mutationFn: deleteAvailabilityBlock,
    onSuccess: () => {
      invalidate()
      setBlockDialogOpen(false)
    },
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
    onSuccess: () => {
      invalidate()
      setBlockDialogOpen(false)
    },
  })
  const deleteOccurrence = useMutation({
    mutationFn: ({ seriesId, occurrenceDate }: { seriesId: string; occurrenceDate: string }) =>
      deleteAvailabilityOccurrence(seriesId, occurrenceDate),
    onSuccess: () => {
      invalidate()
      setBlockDialogOpen(false)
    },
  })

  // D-111: every layer is a toggle over the same one `GET /calendar`
  // response — never a second fetch. `layers` starts `null` until
  // `CalendarLayers` reports its (component-state, D-135) defaults, so
  // nothing is hidden before that first report lands.
  const showMyAvailability = layers?.myAvailability ?? true
  const showSessions = layers?.sessions ?? true

  const occurrences: GridOccurrence[] = showMyAvailability
    ? (calendar.data?.myAvailability.occurrences ?? []).map((occurrence) => ({
        id:
          occurrence.origin.kind === 'block'
            ? (occurrence.origin.blockId ?? '')
            : `${occurrence.origin.seriesId}:${occurrence.origin.occurrenceDate}`,
        start: instantToFakeLocal(occurrence.startTime, timeZone),
        end: instantToFakeLocal(occurrence.endTime, timeZone),
      }))
    : []
  const merged: GridRange[] = showMyAvailability
    ? (calendar.data?.myAvailability.merged ?? []).map((range) => ({
        start: instantToFakeLocal(range.start, timeZone),
        end: instantToFakeLocal(range.end, timeZone),
      }))
    : []

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
  const friendRanges: GridFriendRange[] = showFriendAvailability
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
    : []

  // A-077 at access-kind `any`: the sessions layer already includes
  // inherited/discoverable rows (D-149), each carrying `viewerRelation` so
  // the grid can style them distinctly — never a second fetch or filter.
  const sessionEntries: GridSessionEntry[] = showSessions
    ? (calendar.data?.sessions ?? []).map((session) => ({
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
    : []

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

  function handleCreateBlock(range: GridRange) {
    createBlock.mutate({
      startTime: fakeLocalToInstant(range.start, timeZone),
      endTime: fakeLocalToInstant(range.end, timeZone),
    })
  }

  function handleEditOccurrence(occurrenceId: string) {
    editTrigger.current = document.activeElement as HTMLElement | null
    const raw = (calendar.data?.myAvailability.occurrences ?? []).find((occurrence) => {
      const id =
        occurrence.origin.kind === 'block'
          ? (occurrence.origin.blockId ?? '')
          : `${occurrence.origin.seriesId}:${occurrence.origin.occurrenceDate}`
      return id === occurrenceId
    })
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

  const activeError =
    calendar.error ??
    createBlock.error ??
    editBlock.error ??
    deleteBlock.error ??
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

  return (
    <main
      className="mx-auto max-w-5xl space-y-6 p-4 sm:p-8"
      data-testid="screen-calendar"
    >
      <h1 className="font-display text-3xl font-bold tracking-tight text-ink">
        {t('calendar.heading')}
      </h1>

      {activeError && <p role="alert">{errorMessage(activeError)}</p>}

      <div role="tablist">
        <Link
          aria-selected={!isAgenda}
          className={`mr-2 inline-flex items-center px-4 py-2 bg-card text-ink aria-selected:bg-accent aria-selected:text-accent-ink dark:aria-selected:border-accent-ink ${raisedInteractiveClasses}`}
          role="tab"
          to="/calendar"
        >
          {t('calendar.week')}
        </Link>
        <Link
          aria-selected={isAgenda}
          className={`inline-flex items-center px-4 py-2 bg-card text-ink aria-selected:bg-accent aria-selected:text-accent-ink dark:aria-selected:border-accent-ink ${raisedInteractiveClasses}`}
          role="tab"
          to="/calendar/agenda"
        >
          {t('calendar.agenda')}
        </Link>
      </div>

      <div className="flex items-center gap-2">
        <button
          className={`min-w-11 px-3 text-ink bg-card ${raisedInteractiveClasses}`}
          type="button"
          onClick={() => setWeekOffset((value) => value - 1)}
        >
          {t('calendar.previousWeek')}
        </button>
        <button
          className={`min-w-11 px-3 text-ink bg-card ${raisedInteractiveClasses}`}
          type="button"
          onClick={() => setWeekOffset((value) => value + 1)}
        >
          {t('calendar.nextWeek')}
        </button>
      </div>

      <CalendarLayers friends={friends} onChange={setLayers} />

      {isAgenda ? (
        <AgendaView
          dateLabelFormatter={dateLabelFormatter}
          entries={agendaEntries}
          timeLabelFormatter={timeLabelFormatter}
        />
      ) : (
        <WeekGrid
          dayLabels={dayLabels}
          friendRanges={friendRanges}
          merged={merged}
          occurrences={occurrences}
          sessionEntries={sessionEntries}
          timeLabelFormatter={timeLabelFormatter}
          weekStart={weekStart}
          onCreateBlock={handleCreateBlock}
          onEditOccurrence={handleEditOccurrence}
        />
      )}

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
        triggerRef={editTrigger}
        onDelete={handleDelete}
        onOpenChange={setBlockDialogOpen}
        onSave={handleBlockSave}
      />
    </main>
  )
}
