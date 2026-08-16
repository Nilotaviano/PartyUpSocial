import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import {
  controlRowClasses,
  fieldControlClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
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

/**
 * A-083's poll-arm producer half (design §5): each selected window contributes exactly one
 * option instant — never a duration, which is not an option property (spec §7.3-1) — accumulated
 * into `/polls/new?startTimes=...` as a repeated query param (route query only, never a server
 * draft, D-152/D-162). This function passes the raw window starts through unclamped — the clamp
 * (`RecommendationPrefill.ForPollOption`'s `WouldClamp`) is computed on read, by
 * `PollCreatePage.tsx`'s `readPollPrefill`, the same "re-apply on read, don't trust the URL's own
 * age" pattern `SessionCreatePage.tsx`'s `readPrefill` already uses for the session arm.
 */
function buildPollPrefillUrl(starts: string[]): string {
  const params = new URLSearchParams()
  for (const start of starts) {
    params.append('startTimes', start)
  }
  return `/polls/new?${params.toString()}`
}

export function FindATimePage() {
  const { t, i18n } = useTranslation(['availability', 'sessions', 'errors', 'common'])
  const [selectedFriends, setSelectedFriends] = useState<string[]>([])
  const [rangeDays, setRangeDays] = useState(DEFAULT_RANGE_DAYS)
  const [durationMinutes, setDurationMinutes] = useState(String(DEFAULT_DURATION_MINUTES))
  // A-083 poll arm (design §5): windows the viewer has picked as poll option candidates,
  // keyed by `window.start` — accumulated across the results list, never a single-window pick.
  const [pollWindowStarts, setPollWindowStarts] = useState<string[]>([])

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

  function togglePollWindow(start: string, checked: boolean) {
    setPollWindowStarts((current) =>
      checked ? [...current, start] : current.filter((value) => value !== start),
    )
  }
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
    <main data-testid="screen-find-a-time">
      <PageScaffold>
        <PageHeader description={t('findATime.subheading')} title={t('findATime.heading')} />

        {anyError && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage(anyError)}
          </StatusBanner>
        )}

        <Section
          description={
            selectedFriends.length === 0
              ? t('findATime.friendsAllDefault')
              : t('findATime.friendsSelected', { count: selectedFriends.length })
          }
          title={t('findATime.friendsLabel')}
          titleId="find-a-time-friends-heading"
        >
          {friends.isPending && (
            <p
              className="motion-safe:animate-pulse text-ink-soft"
              data-testid="find-a-time-friends-loading"
              role="status"
            >
              {t('common.loading', { ns: 'common' })}
            </p>
          )}

          <ul className="grid gap-2 sm:grid-cols-2">
            {(friends.data ?? []).map((friend) => (
              <li key={friend.friendshipId}>
                <label className={controlRowClasses}>
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

          {/*
            A-078: the server never echoes a per-id resolution of an explicit
            selection, so an "explicit" response has nothing new to add beyond
            the checkboxes above. An omitted selection ("allFriends") is
            resolved server-side, and `consideredFriends` is the only source of
            "whose calendars produced these windows" — the client never derives
            that set itself. An empty list (no accepted friends yet) renders
            nothing at all rather than a heading over a blank list.
          */}
          {overlaps.data?.selectionSource === 'allFriends' &&
            overlaps.data.consideredFriends &&
            overlaps.data.consideredFriends.length > 0 && (
              <div className="flex flex-col gap-1" data-testid="find-a-time-considered-friends">
                <h3 className="font-display text-meta font-semibold text-ink-soft">
                  {t('findATime.consideredHeading')}
                </h3>
                <ul className="text-meta text-ink-soft">
                  {overlaps.data.consideredFriends.map((friend) => (
                    <li key={friend.ordinal}>
                      {friend.displayName ?? t('detail.anonymous', { ns: 'sessions' })}
                    </li>
                  ))}
                </ul>
              </div>
            )}
        </Section>

        <Section title={t('findATime.rangeLabel')} titleId="find-a-time-range-heading">
          <div className="flex flex-wrap gap-4">
            <div className="flex flex-col gap-1">
              <label className="text-ink" htmlFor="find-a-time-range-days">
                {t('findATime.rangeLabel')}
              </label>
              <input
                className={`${fieldControlClasses} w-28`}
                id="find-a-time-range-days"
                max={92}
                min={1}
                type="number"
                value={rangeDays}
                onChange={(event) => setRangeDays(Number(event.target.value) || DEFAULT_RANGE_DAYS)}
              />
            </div>

            <div className="flex flex-col gap-1">
              <label className="text-ink" htmlFor="find-a-time-duration">
                {t('create.duration', { ns: 'sessions' })}
              </label>
              <input
                className={`${fieldControlClasses} w-28`}
                id="find-a-time-duration"
                min={15}
                step={15}
                type="number"
                value={durationMinutes}
                onChange={(event) => setDurationMinutes(event.target.value)}
              />
            </div>
          </div>
        </Section>

        {/* §12-44: context only — never fed into the overlap search. */}
        <Section
          title={t('calendar.layers.myAvailability', { ns: 'availability' })}
          titleId="find-a-time-mine-heading"
        >
          {myMerged.length > 0 && (
            <ul className="text-meta text-ink-soft">
              {myMerged.map((entry, index) => (
                <li key={index} className="border-t border-ink/25 py-2 first:border-t-0">
                  {timeFormat.format(new Date(entry.start))} –{' '}
                  {timeFormat.format(new Date(entry.end))}
                </li>
              ))}
            </ul>
          )}
        </Section>

        <Section title={t('findATime.resultsHeading')} titleId="find-a-time-results-heading">
          {overlaps.isPending && (
            <p className="motion-safe:animate-pulse text-ink-soft" role="status">
              {t('common.loading', { ns: 'common' })}
            </p>
          )}
          {!overlaps.isPending && windows.length === 0 && <p>{t('findATime.empty')}</p>}
          {windows.length > 0 && (
            <ul data-testid="find-a-time-results">
              {windows.map((window, index) => {
                const finish = new Date(window.start).getTime() + plannedDuration * 60_000
                const overMinutes = Math.max(
                  0,
                  Math.round((finish - new Date(window.end).getTime()) / 60_000),
                )
                return (
                  <li
                    key={`${window.start}-${index}`}
                    className="flex flex-col gap-2 border-t border-ink/25 py-4 first:border-t-0"
                  >
                    <p className="font-display font-semibold text-ink">
                      {timeFormat.format(new Date(window.start))} –{' '}
                      {timeFormat.format(new Date(window.end))}
                    </p>
                    <p className="text-meta text-ink-soft">
                      {t('findATime.available', { count: window.available.length })}
                      {' · '}
                      {t('findATime.maybe', { count: window.maybe.length })}
                    </p>
                    {(window.available.length > 0 || window.maybe.length > 0) && (
                      <div className="flex flex-col gap-2 sm:flex-row sm:gap-6">
                        {window.available.length > 0 && (
                          <div className="flex flex-col gap-1">
                            <h4 className="font-display text-meta font-semibold text-ink-soft">
                              {t('findATime.available', { count: window.available.length })}
                            </h4>
                            <ul className="text-meta text-ink-soft">
                              {window.available.map((participant) => (
                                <li key={participant.ordinal}>
                                  {participant.displayName ?? t('detail.anonymous', { ns: 'sessions' })}
                                </li>
                              ))}
                            </ul>
                          </div>
                        )}
                        {window.maybe.length > 0 && (
                          <div className="flex flex-col gap-1">
                            <h4 className="font-display text-meta font-semibold text-ink-soft">
                              {t('findATime.maybe', { count: window.maybe.length })}
                            </h4>
                            <ul className="text-meta text-ink-soft">
                              {window.maybe.map((participant) => (
                                <li key={participant.ordinal}>
                                  {participant.displayName ?? t('detail.anonymous', { ns: 'sessions' })}
                                </li>
                              ))}
                            </ul>
                          </div>
                        )}
                      </div>
                    )}
                    {overMinutes > 0 && (
                      <p className="text-meta font-semibold text-danger" role="status">
                        {t('findATime.overDurationWarning', { minutes: overMinutes })}
                      </p>
                    )}
                    <div className="flex flex-wrap items-center gap-3">
                      <Link
                        className={`inline-flex items-center px-4 py-2 text-primary-ink bg-primary ${raisedInteractiveClasses}`}
                        to={buildPrefillUrl(window, plannedDuration)}
                      >
                        {t('findATime.scheduleCta')}
                      </Link>
                      <label className={`${controlRowClasses} sm:w-auto`}>
                        <input
                          checked={pollWindowStarts.includes(window.start)}
                          type="checkbox"
                          onChange={(event) => togglePollWindow(window.start, event.target.checked)}
                        />
                        {t('findATime.pollArm.addToPoll')}
                      </label>
                    </div>
                  </li>
                )
              })}
            </ul>
          )}

          {pollWindowStarts.length > 0 && (
            <SectionCard>
              <p className="text-ink">
                {t('findATime.pollArm.selectedCount', { count: pollWindowStarts.length })}
              </p>
              <Link
                className={`inline-flex w-fit items-center px-4 py-2 text-primary-ink bg-primary ${raisedInteractiveClasses}`}
                to={buildPollPrefillUrl(pollWindowStarts)}
              >
                {t('findATime.pollArm.createPollCta')}
              </Link>
            </SectionCard>
          )}
        </Section>
      </PageScaffold>
    </main>
  )
}
