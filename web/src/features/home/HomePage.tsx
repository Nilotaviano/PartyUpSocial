import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  quietChipClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { EmptyState } from '../../components/ui/emptyState'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold, PageSlot } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
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

const rowClasses = 'border-t border-ink/25 first:border-t-0'
const rowLinkClasses =
  'flex flex-col gap-1 rounded-control px-2 py-3 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const seeAllClasses =
  'inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

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
        <PageHeader title={t('home.heading')} />
        <PageSlot />

        {anyError && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage(anyError)}
          </StatusBanner>
        )}

        {/* D-103 region 1 — the attention band (§3.6): a page-scoped triage
            Section, marked by an accent rule only while it actually carries
            work the viewer has to do. */}
        <Section
          className={hasActionable ? 'border-l-4 border-accent pl-4' : undefined}
          data-testid="home-region-actionable"
          title={t('home.actionable.heading')}
          titleId="home-actionable-heading"
        >
          {actionable.isPending && loading()}

          {!actionable.isPending && !hasActionable && !isQuiet && (
            <p className="text-ink-soft">{t('home.actionable.empty')}</p>
          )}

          {!actionable.isPending && !hasActionable && isQuiet && (
            <EmptyState
              action={
                <div className="flex flex-wrap items-center justify-center gap-2">
                  <Link
                    className={`inline-flex items-center gap-2 bg-primary px-4 py-2 text-primary-ink ${raisedInteractiveClasses}`}
                    to="/friends"
                  >
                    {t('emptyState.home.cta')}
                  </Link>
                  <Link
                    className={`inline-flex items-center gap-2 bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
                    to="/sessions/new"
                  >
                    {t('emptyState.home.ctaSecondary')}
                  </Link>
                </div>
              }
              illustration="🎲"
            >
              {t('emptyState.home.body')}
            </EmptyState>
          )}

          {invitations.length > 0 && (
            <div className="flex flex-col gap-2">
              <h3 className="font-display font-semibold text-ink">
                {t('home.actionable.invitationsHeading')}
              </h3>
              <ul>
                {invitations.map(({ session, invitedAt }) => (
                  <li key={session.id} className={rowClasses} data-invited-at={invitedAt}>
                    <div className="flex flex-col gap-2 px-2 py-3">
                      <Link
                        className="flex flex-wrap items-center gap-2 rounded-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                        to={`/sessions/${session.id}`}
                      >
                        <span className="font-display font-semibold text-ink">
                          {session.title}
                        </span>
                        <SessionStatusChip status={session.status} />
                        <span className={quietChipClasses}>
                          {timeFormat.format(new Date(session.startTime))}
                        </span>
                      </Link>
                      <div className="flex flex-wrap gap-2">
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
              <ul>
                {joinRequests.map((request) => (
                  <li key={request.joinRequestId} className={rowClasses}>
                    <div className="flex flex-col gap-2 px-2 py-3">
                      <Link
                        className="flex flex-wrap items-center gap-2 rounded-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                        to={`/sessions/${request.session.id}`}
                      >
                        <span className="font-display font-semibold text-ink">
                          {request.session.title}
                        </span>
                        <SessionStatusChip status={request.session.status} />
                      </Link>
                      <p className="text-ink-soft">
                        {t('home.actionable.requestedBy', {
                          name:
                            request.requester.displayName ??
                            t('detail.anonymous', { ns: 'sessions' }),
                        })}
                      </p>
                      <div className="flex flex-wrap gap-2">
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
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {hasActionable && hasMoreActionable && (
            <Link className={seeAllClasses} to="/sessions/upcoming">
              {t('home.actionable.seeAll')}
            </Link>
          )}
        </Section>

        {/* D-103 region 2 (Phase 6 S7 fill): the involved band's still-polling polls. */}
        <Section
          data-testid="home-region-polls"
          title={t('home.polls.heading')}
          titleId="home-polls-heading"
        >
          {polls.isPending && loading()}
          {/* The one section-empty line that stays: `HomePage.test.tsx` binds it
              as the honest disclosure of the polling-only predicate above. */}
          {!polls.isPending && pollItems.length === 0 && (
            <p className="text-ink-soft">{t('home.polls.empty')}</p>
          )}
          {pollItems.length > 0 && (
            <>
              <ul data-testid="home-polls-list">
                {pollItems.map((poll) => (
                  <li key={poll.id} className={rowClasses}>
                    <Link className={rowLinkClasses} to={`/polls/${poll.id}`}>
                      <span className="font-display font-semibold text-ink">
                        {poll.title}
                      </span>
                      <span className="flex flex-wrap items-center gap-2">
                        <PollStatusChip poll={poll} />
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
              <Link className={seeAllClasses} to="/polls">
                {t('home.polls.seeAll')}
              </Link>
            </>
          )}
        </Section>

        {/* D-103 region 3 */}
        <Section
          data-testid="home-region-upcoming"
          title={t('home.upcoming.heading')}
          titleId="home-upcoming-heading"
        >
          {upcoming.isPending && loading()}
          {upcomingItems.length > 0 && (
            <ul>
              {upcomingItems.map((session) => (
                <li key={session.id} className={rowClasses}>
                  <Link className={rowLinkClasses} to={`/sessions/${session.id}`}>
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="font-display font-semibold text-ink">
                        {session.title}
                      </span>
                      <SessionStatusChip status={session.status} />
                    </span>
                    <span className="flex flex-wrap items-center gap-2">
                      <span className={quietChipClasses}>
                        {timeFormat.format(new Date(session.startTime))}
                      </span>
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
          <Link className={seeAllClasses} to="/sessions/upcoming">
            {t('home.upcoming.seeAll')}
          </Link>
        </Section>

        {/* D-103 region 4 / D-110 / D-116 */}
        <Section
          data-testid="home-region-findatime"
          title={t('findATime.heading', { ns: 'availability' })}
          titleId="home-findatime-heading"
        >
          <div>
            <Link
              className={`inline-flex items-center gap-2 bg-primary px-4 py-2 text-primary-ink ${raisedInteractiveClasses}`}
              to="/find-a-time"
            >
              {t('findATime.heading', { ns: 'availability' })}
            </Link>
          </div>

          {suggestions.isPending && loading()}
          {suggestionItems.length > 0 && (
            <div className="flex flex-col gap-2">
              <h3 className="font-display font-semibold text-ink">
                {t('findATime.suggestionsHeading', { ns: 'availability' })}
              </h3>
              <ul data-testid="home-suggestions-list">
                {suggestionItems.map((window, index) => (
                  <li key={`${window.start}-${index}`} className={rowClasses}>
                    <div className="flex flex-col gap-1 px-2 py-3">
                      <span className="text-ink">
                        {timeFormat.format(new Date(window.start))} –{' '}
                        {timeFormat.format(new Date(window.end))}
                      </span>
                      <span className="text-meta text-ink-soft">
                        {t('findATime.available', {
                          ns: 'availability',
                          count: window.available.length,
                        })}
                      </span>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
          {suggestions.data?.hasMore && (
            <Link className={seeAllClasses} to="/find-a-time">
              {t('findATime.seeAll', { ns: 'availability' })}
            </Link>
          )}
        </Section>

        {/* D-103 region 5 / D-149 */}
        <Section
          data-testid="home-region-discover"
          title={t('home.discoverable.heading')}
          titleId="home-discover-heading"
        >
          {discover.isPending && loading()}
          {!discover.isPending && discoverItems.length === 0 && !isQuiet && (
            <EmptyState
              action={
                <Link
                  className={`inline-flex items-center gap-2 bg-primary px-4 py-2 text-primary-ink ${raisedInteractiveClasses}`}
                  to="/friends"
                >
                  {t('emptyState.discover.cta')}
                </Link>
              }
              illustration="🔭"
            >
              {t('emptyState.discover.body')}
            </EmptyState>
          )}
          {discoverItems.length > 0 && (
            <ul data-testid="home-discover-list">
              {discoverItems.map((session) => (
                <li key={session.id} className={rowClasses}>
                  <Link className={rowLinkClasses} to={`/sessions/${session.id}`}>
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="font-display font-semibold text-ink">
                        {session.title}
                      </span>
                      <SessionStatusChip status={session.status} />
                    </span>
                    <span className="flex flex-wrap items-center gap-2">
                      <span className={quietChipClasses}>
                        {timeFormat.format(new Date(session.startTime))}
                      </span>
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
          <Link className={seeAllClasses} data-tour="discover" to="/discover">
            {t('home.discoverable.seeAll')}
          </Link>
        </Section>
      </PageScaffold>
    </main>
  )
}
