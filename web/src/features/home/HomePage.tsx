import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  AlertCircle,
  CalendarPlus,
  Compass,
  Dices,
  Gamepad2,
  Plus,
  Telescope,
  UserPlus,
  Vote,
} from 'lucide-react'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { PageHeader } from '../../components/shell/PageHeader'
import { EmptyState, SectionCard } from '../../components/shell/SectionCard'
import { PageScaffold, PageSlot } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
import {
  availabilityKeys,
  getHomeSuggestions,
} from '../availability/availabilityQueries'
import { PollStatusChip } from '../polls/PollStatusChip'
import { getPollList, pollKeys, pollWindowState } from '../polls/pollQueries'
import { SessionStatusChip } from '../sessions/SessionStatusChip'
import {
  approveJoinRequest,
  getSessionList,
  rejectJoinRequest,
  respondToInvitation,
  sessionKeys,
  type ParticipationResponse,
} from '../sessions/sessionQueries'
import { getActionableInbox, getDiscoverSessions, homeKeys } from './homeQueries'

const SUGGESTION_LIMIT = 5
const UPCOMING_PREVIEW_LIMIT = 5
const POLLS_PREVIEW_LIMIT = 5

const primaryButtonClasses =
  'inline-flex h-11 items-center justify-center gap-2 rounded-xl bg-primary px-4 font-display text-[13px] font-bold text-primary-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const outlineButtonClasses =
  'inline-flex h-11 items-center justify-center gap-2 rounded-xl border border-border bg-background px-4 font-display text-[13px] font-bold text-foreground hover:bg-muted/60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const ghostSeeAllClasses =
  'inline-flex h-10 shrink-0 items-center justify-center rounded-lg px-3 font-display text-[13px] font-bold text-foreground hover:bg-muted/60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const itemCardClasses =
  'block rounded-xl border border-border bg-background p-3 hover:bg-muted/60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const itemTitleClasses = 'truncate font-display text-[15px] font-extrabold'
const itemMetaClasses = 'mt-1 flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground'
const triageRowClasses =
  'grid grid-cols-[auto_minmax(0,1fr)] items-center gap-3 rounded-xl border border-border bg-background px-3 py-2.5 hover:bg-muted/60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const triageIconClasses =
  'grid size-9 shrink-0 place-items-center rounded-full bg-maybe text-maybe-foreground'
const triageTitleClasses = 'block truncate font-display text-[14px] font-bold'
const triageMetaClasses = 'block truncate text-[12px] text-muted-foreground'

/**
 * D-103's eyebrow: today plus the Monday–Sunday week it falls in, formatted
 * for the viewer's language via `Intl.DateTimeFormat` — never a hardcoded
 * English string. The week boundary is fixed at Monday (product decision,
 * not a locale-detected first-day-of-week) to match the calendar feature's
 * own week convention (`startOfLocaleWeek`'s ISO default in
 * `features/availability/lovable/zone.ts`).
 */
function formatEyebrow(t: TFunction, locale: string, now: Date): string {
  const dayOfWeek = now.getDay() // 0 = Sunday .. 6 = Saturday
  const diffToMonday = (dayOfWeek + 6) % 7
  const monday = new Date(now)
  monday.setDate(now.getDate() - diffToMonday)
  const sunday = new Date(monday)
  sunday.setDate(monday.getDate() + 6)

  const sameMonth = monday.getMonth() === sunday.getMonth()
  const todayText = new Intl.DateTimeFormat(locale, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
  }).format(now)
  const weekStartText = new Intl.DateTimeFormat(
    locale,
    sameMonth ? { day: 'numeric' } : { day: 'numeric', month: 'short' },
  ).format(monday)
  const weekEndText = new Intl.DateTimeFormat(locale, {
    day: 'numeric',
    month: 'short',
  }).format(sunday)

  return t('home.eyebrow', {
    today: todayText,
    weekStart: weekStartText,
    weekEnd: weekEndText,
  })
}

export function HomePage() {
  const { t, i18n } = useTranslation([
    'home',
    'sessions',
    'availability',
    'polls',
    'errors',
    'common',
  ])
  const queryClient = useQueryClient()

  // D-103 region 1: pending invitations and organizer join requests, the
  // one read `P5-HOME-01` adds for exactly this region (§5.1/§8).
  const actionable = useQuery({
    queryKey: homeKeys.actionable,
    queryFn: getActionableInbox,
  })

  // D-103 region 3: shipped upcoming-list data, previewed here.
  const upcoming = useQuery({
    queryKey: sessionKeys.list('upcoming'),
    queryFn: () => getSessionList('upcoming'),
  })

  // D-103 region 4 / D-110 / D-116: S6's suggestions hook, consumed only.
  const suggestions = useQuery({
    queryKey: availabilityKeys.suggestions(),
    queryFn: getHomeSuggestions,
  })

  // D-103 region 5 / D-149: the first page of Discover.
  const discover = useQuery({
    queryKey: homeKeys.discover(),
    queryFn: () => getDiscoverSessions(),
  })

  // D-103 region 2 (Phase 6 S7 fill, PRODUCT_SPEC:434 region 2): the involved band's first
  // page, previewed here. `PollSummaryResponse` carries no per-option own-vote data (only
  // `optionCount`) — the honest predicate this wire shape supports is "still accepting votes"
  // (`pollWindowState === 'polling'`), not the stronger "has specifically unvoted options",
  // which would need a per-poll detail fetch this band idiom doesn't make.
  const polls = useQuery({
    queryKey: pollKeys.list('involved'),
    queryFn: () => getPollList('involved'),
  })

  async function afterActionableMutation() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: homeKeys.actionable }),
      queryClient.invalidateQueries({ queryKey: ['sessions'] }),
    ])
  }

  const respond = useMutation({
    mutationFn: ({
      sessionId,
      response,
    }: {
      sessionId: string
      response: ParticipationResponse
    }) => respondToInvitation(sessionId, response),
    onSuccess: afterActionableMutation,
  })

  const approve = useMutation({
    mutationFn: ({
      sessionId,
      requestId,
    }: {
      sessionId: string
      requestId: string
    }) => approveJoinRequest(sessionId, requestId),
    onSuccess: afterActionableMutation,
  })

  const reject = useMutation({
    mutationFn: ({
      sessionId,
      requestId,
    }: {
      sessionId: string
      requestId: string
    }) => rejectJoinRequest(sessionId, requestId),
    onSuccess: afterActionableMutation,
  })

  function errorMessage(error: unknown): string {
    if (!isApiError(error)) return t('generic', { ns: 'errors' })
    return t(error.code, {
      ns: 'errors',
      defaultValue: t('generic', { ns: 'errors' }),
    })
  }

  const anyError =
    actionable.error ??
    upcoming.error ??
    suggestions.error ??
    discover.error ??
    polls.error ??
    respond.error ??
    approve.error ??
    reject.error

  // §10.1: region 1's invitation rows render from `viewerRelation == "invitee"`
  // — the server-shipped contract already scopes `invitations[]` to that
  // relation, but the client re-asserts it rather than trusting the array's
  // name alone, so a differently-related row (e.g. already `Going`) never
  // renders here with respond actions.
  const invitations = (actionable.data?.invitations ?? []).filter(
    ({ session }) => session.viewerRelation === 'invitee',
  )
  const joinRequests = actionable.data?.joinRequests ?? []
  const hasActionable = invitations.length > 0 || joinRequests.length > 0
  const hasMoreActionable = Boolean(
    actionable.data?.hasMoreInvitations || actionable.data?.hasMoreJoinRequests,
  )

  const pollItems = (polls.data?.items ?? [])
    .filter((poll) => pollWindowState(poll) === 'polling')
    .slice(0, POLLS_PREVIEW_LIMIT)
  const upcomingItems = (upcoming.data?.items ?? []).slice(0, UPCOMING_PREVIEW_LIMIT)
  const suggestionItems = (suggestions.data?.suggestions ?? []).slice(0, SUGGESTION_LIMIT)
  const discoverItems = discover.data?.items ?? []

  // §3.6: a fully empty page is a quiet page — it gets one empty state with a
  // way out instead of a run of "nothing here" lines. Any populated section
  // means the page has a subject, so the sections speak for themselves.
  const settled =
    !actionable.isPending &&
    !polls.isPending &&
    !upcoming.isPending &&
    !discover.isPending
  const sectionsWithContent = [
    hasActionable,
    pollItems.length > 0,
    upcomingItems.length > 0,
    discoverItems.length > 0,
  ].filter(Boolean).length
  const isQuiet = settled && sectionsWithContent === 0

  const timeFormat = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

  function loading() {
    return (
      <p role="status" className="motion-safe:animate-pulse text-ink-soft">
        {t('common.loading', { ns: 'common' })}
      </p>
    )
  }

  return (
    <main data-testid="screen-app">
      <PageScaffold>
        <PageHeader
          title={t('home.heading')}
          eyebrow={formatEyebrow(t, i18n.language, new Date())}
          description={t('home.description')}
          actions={
            <>
              <Link className={primaryButtonClasses} to="/polls/new">
                <Plus aria-hidden className="size-4" />
                {t('home.actions.newPoll')}
              </Link>
              <Link className={outlineButtonClasses} to="/find-a-time">
                <CalendarPlus aria-hidden className="size-4" />
                {t('home.actions.findATime')}
              </Link>
            </>
          }
        />
        <PageSlot />

        {anyError && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage(anyError)}
          </StatusBanner>
        )}

        <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_400px] lg:items-start">
          <div className="space-y-4">
            {/* D-103 region 1 — the attention band (§3.6): a page-scoped triage
                Section, marked by an accent rule only while it actually carries
                work the viewer has to do. */}
            <div
              aria-label={t('home.actionable.heading')}
              className={hasActionable ? 'border-l-4 border-accent pl-4' : undefined}
              data-testid="home-region-actionable"
              role="region"
            >
              <SectionCard icon={AlertCircle} title={t('home.actionable.heading')}>
                {actionable.isPending && loading()}

                {!actionable.isPending && !hasActionable && !isQuiet && (
                  <p className="text-ink-soft">{t('home.actionable.empty')}</p>
                )}

                {!actionable.isPending && !hasActionable && isQuiet && (
                  <EmptyState
                    action={
                      <div className="flex flex-wrap items-center justify-center gap-2">
                        <Link className={primaryButtonClasses} to="/friends">
                          {t('emptyState.home.cta')}
                        </Link>
                        <Link className={outlineButtonClasses} to="/sessions/new">
                          {t('emptyState.home.ctaSecondary')}
                        </Link>
                      </div>
                    }
                    description={t('emptyState.home.description')}
                    icon={Dices}
                    title={t('emptyState.home.title')}
                  />
                )}

                {invitations.length > 0 && (
                  <div className="flex flex-col gap-2">
                    <h3 className="font-display font-semibold text-ink">
                      {t('home.actionable.invitationsHeading')}
                    </h3>
                    <ul className="space-y-2">
                      {invitations.map(({ session, invitedAt }) => (
                        <li key={session.id} data-invited-at={invitedAt}>
                          <Link className={triageRowClasses} to={`/sessions/${session.id}`}>
                            <span className={triageIconClasses}>
                              <Gamepad2 aria-hidden className="size-4" />
                            </span>
                            <span className="min-w-0">
                              <span className={triageTitleClasses}>{session.title}</span>
                              <span className={triageMetaClasses}>
                                {timeFormat.format(new Date(session.startTime))}
                              </span>
                            </span>
                          </Link>
                          <div className="mt-2 flex flex-wrap items-center gap-2 pl-12">
                            <SessionStatusChip status={session.status} />
                            {(['Going', 'Maybe', 'Declined'] as const).map((choice) => (
                              <Button
                                key={choice}
                                disabled={respond.isPending}
                                type="button"
                                variant="neutral"
                                onClick={() =>
                                  respond.mutate({ sessionId: session.id, response: choice })
                                }
                              >
                                {t(`rsvp.${choice.toLowerCase()}`, { ns: 'sessions' })}
                              </Button>
                            ))}
                          </div>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {joinRequests.length > 0 && (
                  <div className="flex flex-col gap-2">
                    <h3 className="font-display font-semibold text-ink">
                      {t('home.actionable.joinRequestsHeading')}
                    </h3>
                    <ul className="space-y-2">
                      {joinRequests.map((request) => (
                        <li key={request.joinRequestId}>
                          <Link
                            className={triageRowClasses}
                            to={`/sessions/${request.session.id}`}
                          >
                            <span className={triageIconClasses}>
                              <UserPlus aria-hidden className="size-4" />
                            </span>
                            <span className="min-w-0">
                              <span className={triageTitleClasses}>
                                {request.session.title}
                              </span>
                              <span className={triageMetaClasses}>
                                {t('home.actionable.requestedBy', {
                                  name:
                                    request.requester.displayName ??
                                    t('detail.anonymous', { ns: 'sessions' }),
                                })}
                              </span>
                            </span>
                          </Link>
                          <div className="mt-2 flex flex-wrap items-center gap-2 pl-12">
                            <SessionStatusChip status={request.session.status} />
                            <Button
                              disabled={approve.isPending}
                              type="button"
                              variant="neutral"
                              onClick={() =>
                                approve.mutate({
                                  sessionId: request.session.id,
                                  requestId: request.joinRequestId,
                                })
                              }
                            >
                              {t('join.approve', { ns: 'sessions' })}
                            </Button>
                            <Button
                              disabled={reject.isPending}
                              type="button"
                              variant="neutral"
                              onClick={() =>
                                reject.mutate({
                                  sessionId: request.session.id,
                                  requestId: request.joinRequestId,
                                })
                              }
                            >
                              {t('join.reject', { ns: 'sessions' })}
                            </Button>
                          </div>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {hasActionable && hasMoreActionable && (
                  <Link className={ghostSeeAllClasses} to="/sessions/upcoming">
                    {t('home.actionable.seeAll')}
                  </Link>
                )}
              </SectionCard>
            </div>

            {/* D-103 region 2 (Phase 6 S7 fill): the involved band's still-polling polls. */}
            <div
              aria-label={t('home.polls.heading')}
              data-testid="home-region-polls"
              role="region"
            >
              <SectionCard
                action={
                  pollItems.length > 0 && (
                    <Link className={ghostSeeAllClasses} to="/polls">
                      {t('home.polls.seeAll')}
                    </Link>
                  )
                }
                icon={Vote}
                title={t('home.polls.heading')}
              >
                {polls.isPending && loading()}
                {/* The one section-empty line that stays: `HomePage.test.tsx` binds it
                    as the honest disclosure of the polling-only predicate above. */}
                {!polls.isPending && pollItems.length === 0 && (
                  <p className="text-ink-soft">{t('home.polls.empty')}</p>
                )}
                {pollItems.length > 0 && (
                  <ul className="space-y-2" data-testid="home-polls-list">
                    {pollItems.map((poll) => (
                      <li key={poll.id}>
                        <Link className={itemCardClasses} to={`/polls/${poll.id}`}>
                          <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
                            <span className={itemTitleClasses}>{poll.title}</span>
                            <PollStatusChip poll={poll} />
                          </div>
                        </Link>
                      </li>
                    ))}
                  </ul>
                )}
              </SectionCard>
            </div>
          </div>

          <div className="space-y-4">
            {/* D-103 region 3 */}
            <div
              aria-label={t('home.upcoming.heading')}
              data-testid="home-region-upcoming"
              role="region"
            >
              <SectionCard
                action={
                  <Link className={ghostSeeAllClasses} to="/sessions/upcoming">
                    {t('home.upcoming.seeAll')}
                  </Link>
                }
                icon={Gamepad2}
                title={t('home.upcoming.heading')}
              >
                {upcoming.isPending && loading()}
                {upcomingItems.length > 0 && (
                  <ul className="space-y-2">
                    {upcomingItems.map((session) => (
                      <li key={session.id}>
                        <Link className={itemCardClasses} to={`/sessions/${session.id}`}>
                          <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
                            <span className={itemTitleClasses}>{session.title}</span>
                            <SessionStatusChip status={session.status} />
                          </div>
                          <p className={`${itemMetaClasses} tabular-nums`}>
                            {timeFormat.format(new Date(session.startTime))}
                          </p>
                        </Link>
                      </li>
                    ))}
                  </ul>
                )}
              </SectionCard>
            </div>

            {/* D-103 region 4 / D-110 / D-116 */}
            <div
              aria-label={t('findATime.heading', { ns: 'availability' })}
              data-testid="home-region-findatime"
              role="region"
            >
              <SectionCard
                action={
                  suggestions.data?.hasMore && (
                    <Link className={ghostSeeAllClasses} to="/find-a-time">
                      {t('findATime.seeAll', { ns: 'availability' })}
                    </Link>
                  )
                }
                icon={CalendarPlus}
                title={t('findATime.heading', { ns: 'availability' })}
              >
                <div>
                  <Link className={outlineButtonClasses} to="/find-a-time">
                    <CalendarPlus aria-hidden className="size-4" />
                    {t('findATime.heading', { ns: 'availability' })}
                  </Link>
                </div>

                {suggestions.isPending && loading()}
                {suggestionItems.length > 0 && (
                  <div className="mt-3 flex flex-col gap-2">
                    <h3 className="font-display font-semibold text-ink">
                      {t('findATime.suggestionsHeading', { ns: 'availability' })}
                    </h3>
                    <ul className="space-y-2" data-testid="home-suggestions-list">
                      {suggestionItems.map((window, index) => (
                        <li
                          className="rounded-xl border border-border bg-background p-3"
                          key={`${window.start}-${index}`}
                        >
                          <p className={`${itemTitleClasses} tabular-nums`}>
                            {timeFormat.format(new Date(window.start))} –{' '}
                            {timeFormat.format(new Date(window.end))}
                          </p>
                          <p className={itemMetaClasses}>
                            {t('findATime.available', {
                              ns: 'availability',
                              count: window.available.length,
                            })}
                          </p>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </SectionCard>
            </div>

            {/* D-103 region 5 / D-149 */}
            <div
              aria-label={t('home.discoverable.heading')}
              data-testid="home-region-discover"
              role="region"
            >
              <SectionCard
                action={
                  <Link
                    className={ghostSeeAllClasses}
                    data-tour="discover"
                    to="/discover"
                  >
                    {t('home.discoverable.seeAll')}
                  </Link>
                }
                icon={Compass}
                title={t('home.discoverable.heading')}
              >
                {discover.isPending && loading()}
                {!discover.isPending && discoverItems.length === 0 && !isQuiet && (
                  <EmptyState
                    action={
                      <Link className={outlineButtonClasses} to="/friends">
                        <UserPlus aria-hidden className="size-4" />
                        {t('emptyState.discover.cta')}
                      </Link>
                    }
                    description={t('emptyState.discover.body')}
                    icon={Telescope}
                    title={t('discover.empty')}
                  />
                )}
                {discoverItems.length > 0 && (
                  <ul className="space-y-2" data-testid="home-discover-list">
                    {discoverItems.map((session) => (
                      <li key={session.id}>
                        <Link className={itemCardClasses} to={`/sessions/${session.id}`}>
                          <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
                            <span className={itemTitleClasses}>{session.title}</span>
                            <SessionStatusChip status={session.status} />
                          </div>
                          <p className={`${itemMetaClasses} tabular-nums`}>
                            {timeFormat.format(new Date(session.startTime))}
                          </p>
                        </Link>
                      </li>
                    ))}
                  </ul>
                )}
              </SectionCard>
            </div>
          </div>
        </div>
      </PageScaffold>
    </main>
  )
}
