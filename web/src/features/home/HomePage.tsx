import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import {
  availabilityKeys,
  getHomeSuggestions,
} from '../availability/availabilityQueries'
import { getPollList, pollKeys, pollWindowState } from '../polls/pollQueries'
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
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

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

  const timeFormat = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

  return (
    <main
      className="mx-auto max-w-4xl space-y-8 p-4 sm:p-8"
      data-testid="screen-app"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('home.heading')}
      </h1>

      {anyError && (
        <p role="alert">{errorMessage(anyError)}</p>
      )}

      {/* D-103 region 1 */}
      <section
        aria-labelledby="home-actionable-heading"
        data-testid="home-region-actionable"
      >
        <h2 id="home-actionable-heading" className="text-h2 font-display font-semibold text-ink">
          {t('home.actionable.heading')}
        </h2>

        {actionable.isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}

        {!actionable.isPending && !hasActionable && (
          <p className="text-ink-soft">{t('home.actionable.empty')}</p>
        )}

        {invitations.length > 0 && (
          <div className="mt-3">
            <h3 className="font-display font-semibold text-ink">
              {t('home.actionable.invitationsHeading')}
            </h3>
            <ul className="mt-2 space-y-2">
              {invitations.map(({ session, invitedAt }) => (
                <li
                  key={session.id}
                  className="rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
                  data-invited-at={invitedAt}
                >
                  <Link
                    className="font-medium text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    to={`/sessions/${session.id}`}
                  >
                    {session.title}
                  </Link>
                  <div className="mt-2 flex flex-wrap gap-2">
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
          <div className="mt-3">
            <h3 className="font-display font-semibold text-ink">
              {t('home.actionable.joinRequestsHeading')}
            </h3>
            <ul className="mt-2 space-y-2">
              {joinRequests.map((request) => (
                <li
                  key={request.joinRequestId}
                  className="rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
                >
                  <Link
                    className="font-medium text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    to={`/sessions/${request.session.id}`}
                  >
                    {request.session.title}
                  </Link>
                  <p className="text-ink-soft">
                    {t('home.actionable.requestedBy', {
                      name: request.requester.displayName ?? t('detail.anonymous', { ns: 'sessions' }),
                    })}
                  </p>
                  <div className="mt-2 flex flex-wrap gap-2">
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
          <Link
            className="mt-3 inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            to="/sessions/upcoming"
          >
            {t('home.actionable.seeAll')}
          </Link>
        )}
      </section>

      {/* D-103 region 2 (Phase 6 S7 fill): the involved band's still-polling polls. */}
      <section
        aria-labelledby="home-polls-heading"
        data-testid="home-region-polls"
      >
        <h2 id="home-polls-heading" className="text-h2 font-display font-semibold text-ink">
          {t('home.polls.heading')}
        </h2>
        {polls.isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}
        {!polls.isPending && pollItems.length === 0 && (
          <p className="text-ink-soft">{t('home.polls.empty')}</p>
        )}
        {pollItems.length > 0 && (
          <ul className="mt-2 space-y-2" data-testid="home-polls-list">
            {pollItems.map((poll) => (
              <li key={poll.id}>
                <Link
                  className="block rounded-card border-2 border-ink bg-card p-3 shadow-offset-card focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                  to={`/polls/${poll.id}`}
                >
                  <p className="font-medium text-ink">{poll.title}</p>
                  <p className="text-ink-soft">
                    {t('status.pollingUntil', {
                      ns: 'polls',
                      deadline: timeFormat.format(new Date(poll.deadline)),
                    })}
                  </p>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {pollItems.length > 0 && (
          <Link
            className="mt-3 inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            to="/polls"
          >
            {t('home.polls.seeAll')}
          </Link>
        )}
      </section>

      {/* D-103 region 3 */}
      <section
        aria-labelledby="home-upcoming-heading"
        data-testid="home-region-upcoming"
      >
        <h2 id="home-upcoming-heading" className="text-h2 font-display font-semibold text-ink">
          {t('home.upcoming.heading')}
        </h2>
        {upcoming.isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}
        {!upcoming.isPending && upcomingItems.length === 0 && (
          <p className="text-ink-soft">{t('home.upcoming.empty')}</p>
        )}
        {upcomingItems.length > 0 && (
          <ul className="mt-2 space-y-2">
            {upcomingItems.map((session) => (
              <li key={session.id}>
                <Link
                  className="block rounded-card border-2 border-ink bg-card p-3 shadow-offset-card focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                  to={`/sessions/${session.id}`}
                >
                  <p className="font-medium text-ink">{session.title}</p>
                  <p className="text-ink-soft">
                    {timeFormat.format(new Date(session.startTime))}
                  </p>
                </Link>
              </li>
            ))}
          </ul>
        )}
        <Link
          className="mt-3 inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          to="/sessions/upcoming"
        >
          {t('home.upcoming.seeAll')}
        </Link>
      </section>

      {/* D-103 region 4 / D-110 / D-116 */}
      <section
        aria-labelledby="home-findatime-heading"
        data-testid="home-region-findatime"
      >
        <h2 id="home-findatime-heading" className="text-h2 font-display font-semibold text-ink">
          {t('findATime.heading', { ns: 'availability' })}
        </h2>
        <Link
          className={`mt-1 inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
          to="/find-a-time"
        >
          {t('findATime.heading', { ns: 'availability' })}
        </Link>

        <h3 className="mt-4 font-display font-semibold text-ink">
          {t('findATime.suggestionsHeading', { ns: 'availability' })}
        </h3>
        {suggestions.isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}
        {!suggestions.isPending && suggestionItems.length === 0 && (
          <p className="text-ink-soft">{t('findATime.empty', { ns: 'availability' })}</p>
        )}
        {suggestionItems.length > 0 && (
          <ul className="mt-2 space-y-2" data-testid="home-suggestions-list">
            {suggestionItems.map((window, index) => (
              <li
                key={`${window.start}-${index}`}
                className="rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
              >
                <p className="text-ink">
                  {timeFormat.format(new Date(window.start))} –{' '}
                  {timeFormat.format(new Date(window.end))}
                </p>
                <p className="text-ink-soft">
                  {t('findATime.available', { ns: 'availability', count: window.available.length })}
                </p>
              </li>
            ))}
          </ul>
        )}
        {suggestions.data?.hasMore && (
          <Link
            className="mt-3 inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            to="/find-a-time"
          >
            {t('findATime.seeAll', { ns: 'availability' })}
          </Link>
        )}
      </section>

      {/* D-103 region 5 / D-149 */}
      <section
        aria-labelledby="home-discover-heading"
        data-testid="home-region-discover"
      >
        <h2 id="home-discover-heading" className="text-h2 font-display font-semibold text-ink">
          {t('home.discoverable.heading')}
        </h2>
        {discover.isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}
        {!discover.isPending && discoverItems.length === 0 && (
          <p className="text-ink-soft">{t('home.discoverable.empty')}</p>
        )}
        {discoverItems.length > 0 && (
          <ul className="mt-2 space-y-2" data-testid="home-discover-list">
            {discoverItems.map((session) => (
              <li key={session.id}>
                <Link
                  className="block rounded-card border-2 border-ink bg-card p-3 shadow-offset-card focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                  to={`/sessions/${session.id}`}
                >
                  <p className="font-medium text-ink">{session.title}</p>
                  <p className="text-ink-soft">
                    {timeFormat.format(new Date(session.startTime))}
                  </p>
                </Link>
              </li>
            ))}
          </ul>
        )}
        <Link
          className="mt-3 inline-flex min-h-11 items-center font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          to="/discover"
        >
          {t('home.discoverable.seeAll')}
        </Link>
      </section>
    </main>
  )
}
