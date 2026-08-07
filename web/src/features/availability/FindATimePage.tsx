import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { friendKeys, getFriends } from '../friends/friendGraph'
import {
  availabilityKeys,
  findOverlapWindows,
  getMyAvailability,
  type OverlapWindowView,
} from './availabilityQueries'

const DAY_MS = 24 * 60 * 60 * 1000
const DEFAULT_RANGE_DAYS = 7
const DEFAULT_DURATION_MINUTES = 120

// The five new §8 problem codes render from `availability.json`; everything
// else — including the shared `rate_limited`/`transient_conflict` — reads
// from the flat `errors` namespace with its `generic` fallback (§10.2, the
// shipped guest-page convention).
const AVAILABILITY_PROBLEM_CODES = new Set([
  'availability_not_found',
  'availability_validation',
  'invalid_range',
  'range_too_large',
  'occurrence_not_found',
])

/**
 * A-083's producer half: builds `/sessions/new?startTime&durationMinutes&overDurationMinutes`
 * from a recommended window and the intended session duration, mirroring
 * `RecommendationPrefill.ForSession`'s D-109 over-duration arithmetic
 * (`end = start + duration`, overage is how far that end runs past the
 * window). `SessionCreatePage` (S6) re-applies the D-130 clamp itself, so
 * this never clamps the start on the client's behalf.
 */
function buildPrefillUrl(
  window: OverlapWindowView,
  durationMinutes: number,
): string {
  const start = new Date(window.start)
  const end = new Date(window.end)
  const finish = new Date(start.getTime() + durationMinutes * 60_000)
  const overDurationMinutes = Math.max(
    0,
    Math.round((finish.getTime() - end.getTime()) / 60_000),
  )
  const params = new URLSearchParams({
    startTime: window.start,
    durationMinutes: String(durationMinutes),
    overDurationMinutes: String(overDurationMinutes),
  })
  return `/sessions/new?${params.toString()}`
}

export function FindATimePage() {
  const { t, i18n } = useTranslation(['availability', 'sessions', 'errors', 'common'])
  const heading = useRef<HTMLHeadingElement>(null)
  const [selectedFriends, setSelectedFriends] = useState<string[]>([])
  const [rangeDays, setRangeDays] = useState(DEFAULT_RANGE_DAYS)
  const [durationMinutes, setDurationMinutes] = useState(String(DEFAULT_DURATION_MINUTES))

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const friends = useQuery({ queryKey: friendKeys.friends, queryFn: getFriends })

  // D-108/§12-27: defaults to 7 days, same bound as the server's own default.
  const range = useMemo(() => {
    const now = new Date()
    const to = new Date(now.getTime() + Math.max(1, rangeDays) * DAY_MS)
    return { from: now.toISOString(), to: to.toISOString() }
  }, [rangeDays])

  // §12-44: the viewer's own availability is a context strip only, never an
  // overlap-search input.
  const myAvailability = useQuery({
    queryKey: availabilityKeys.me(range.from, range.to),
    queryFn: () => getMyAvailability(range.from, range.to),
  })

  const overlaps = useQuery({
    queryKey: availabilityKeys.overlaps(selectedFriends, range.from, range.to),
    queryFn: () =>
      findOverlapWindows(
        selectedFriends.length > 0 ? selectedFriends : undefined,
        range.from,
        range.to,
      ),
  })

  function errorMessage(error: unknown): string {
    if (!isApiError(error)) return t('generic', { ns: 'errors' })
    if (AVAILABILITY_PROBLEM_CODES.has(error.code)) {
      return t(`errors.codes.${error.code}`)
    }
    return t(error.code, {
      ns: 'errors',
      defaultValue: t('generic', { ns: 'errors' }),
    })
  }

  const anyError = friends.error ?? myAvailability.error ?? overlaps.error
  const plannedDuration = Number(durationMinutes) || 0
  // D-035/D-052: windows render exactly as the server ranked them — Available
  // count first, Maybe count as tie-breaker — no client re-sort.
  const windows = overlaps.data?.windows ?? []
  const myMerged = myAvailability.data?.merged ?? []

  const timeFormat = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-find-a-time"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {t('findATime.heading')}
      </h1>
      <p className="text-slate-600">{t('findATime.subheading')}</p>

      {anyError && <p role="alert">{errorMessage(anyError)}</p>}

      <section aria-labelledby="find-a-time-friends-heading">
        <h2 id="find-a-time-friends-heading" className="text-xl font-semibold text-slate-950">
          {t('findATime.friendsLabel')}
        </h2>
        <p className="text-sm text-slate-600">
          {selectedFriends.length === 0
            ? t('findATime.friendsAllDefault')
            : t('findATime.friendsSelected', { count: selectedFriends.length })}
        </p>
        <ul className="mt-2 space-y-1">
          {(friends.data ?? []).map((friend) => (
            <li key={friend.friendshipId}>
              <label className="inline-flex min-h-11 items-center gap-2">
                <input
                  checked={selectedFriends.includes(friend.userId)}
                  type="checkbox"
                  onChange={(event) => {
                    const checked = event.target.checked
                    setSelectedFriends((current) =>
                      checked
                        ? [...current, friend.userId]
                        : current.filter((id) => id !== friend.userId),
                    )
                  }}
                />
                {friend.displayName} (@{friend.username})
              </label>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="find-a-time-range-heading">
        <h2 id="find-a-time-range-heading" className="text-xl font-semibold text-slate-950">
          {t('findATime.rangeLabel')}
        </h2>
        <label className="mt-2 block" htmlFor="find-a-time-range-days">
          {t('findATime.rangeLabel')}
        </label>
        <input
          className="mt-1 min-h-11 w-24 rounded-md border border-slate-300 px-3 py-2"
          id="find-a-time-range-days"
          max={92}
          min={1}
          type="number"
          value={rangeDays}
          onChange={(event) => setRangeDays(Number(event.target.value) || DEFAULT_RANGE_DAYS)}
        />

        <label className="mt-3 block" htmlFor="find-a-time-duration">
          {t('create.duration', { ns: 'sessions' })}
        </label>
        <input
          className="mt-1 min-h-11 w-24 rounded-md border border-slate-300 px-3 py-2"
          id="find-a-time-duration"
          min={15}
          step={15}
          type="number"
          value={durationMinutes}
          onChange={(event) => setDurationMinutes(event.target.value)}
        />
      </section>

      {/* §12-44: context only — never fed into the overlap search. */}
      <section aria-labelledby="find-a-time-mine-heading">
        <h2 id="find-a-time-mine-heading" className="text-xl font-semibold text-slate-950">
          {t('calendar.layers.myAvailability', { ns: 'availability' })}
        </h2>
        {myMerged.length > 0 && (
          <ul className="mt-2 space-y-1 text-sm text-slate-600">
            {myMerged.map((entry, index) => (
              <li key={index}>
                {timeFormat.format(new Date(entry.start))} –{' '}
                {timeFormat.format(new Date(entry.end))}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="find-a-time-results-heading">
        <h2 id="find-a-time-results-heading" className="text-xl font-semibold text-slate-950">
          {t('findATime.resultsHeading')}
        </h2>
        {overlaps.isPending && (
          <p role="status">{t('common.loading', { ns: 'common' })}</p>
        )}
        {!overlaps.isPending && windows.length === 0 && <p>{t('findATime.empty')}</p>}
        {windows.length > 0 && (
          <ul className="mt-2 space-y-3" data-testid="find-a-time-results">
            {windows.map((window, index) => {
              const finish = new Date(window.start).getTime() + plannedDuration * 60_000
              const overMinutes = Math.max(
                0,
                Math.round((finish - new Date(window.end).getTime()) / 60_000),
              )
              return (
                <li
                  key={`${window.start}-${index}`}
                  className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"
                >
                  <p className="font-semibold text-slate-950">
                    {timeFormat.format(new Date(window.start))} –{' '}
                    {timeFormat.format(new Date(window.end))}
                  </p>
                  <p className="text-sm text-slate-600">
                    {t('findATime.available', { count: window.available.length })}
                    {' · '}
                    {t('findATime.maybe', { count: window.maybe.length })}
                  </p>
                  {overMinutes > 0 && (
                    <p role="status" className="mt-1 text-sm text-amber-700">
                      {t('findATime.overDurationWarning', { minutes: overMinutes })}
                    </p>
                  )}
                  <Link
                    className="mt-3 inline-flex min-h-11 items-center rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                    to={buildPrefillUrl(window, plannedDuration)}
                  >
                    {t('findATime.scheduleCta')}
                  </Link>
                </li>
              )
            })}
          </ul>
        )}
      </section>
    </main>
  )
}
