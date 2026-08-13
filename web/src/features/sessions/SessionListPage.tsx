import { useQueries, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  chipClasses,
  quietChipClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { EmptyState } from '../../components/ui/emptyState'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { SessionStatusChip } from './SessionStatusChip'
import {
  getSessionList,
  getViewerProfile,
  isSessionInProgress,
  sessionKeys,
  viewerProfileKey,
  type SessionScope,
} from './sessionQueries'

type SessionListPageProps = {
  scope: SessionScope
}

export function SessionListPage({ scope }: SessionListPageProps) {
  const { t, i18n } = useTranslation(['sessions', 'errors', 'common'])

  // D-038/D-046: times render in the viewer's profile time zone, not the
  // browser's, falling back to the browser zone only until the profile
  // query resolves.
  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const viewerTimeZone = profile.data?.timeZone ?? undefined

  // Each scope is a distinct route in App.tsx, so react-router mounts a
  // fresh SessionListPage instance per scope: cursors always starts at the
  // first page for a given mounted instance, with no reset-on-prop effect
  // needed. Cursors grow only from an explicit "load more" click, and every
  // requested page keeps the exact ['sessions', scope, cursor] key (§10.1).
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: sessionKeys.list(scope, cursor),
      queryFn: () => getSessionList(scope, cursor),
    })),
  })

  const headingText = scope === 'upcoming' ? t('list.upcoming') : t('list.history')
  const emptyText = scope === 'upcoming' ? t('list.emptyUpcoming') : t('list.emptyHistory')
  const items = pages.flatMap((page) => page.data?.items ?? [])
  const isPending = pages[0]?.isPending ?? true
  const error = pages.find((page) => page.error)?.error
  const lastPage = pages[pages.length - 1]
  const nextCursor = lastPage?.data?.nextCursor ?? undefined
  const isFetchingNextPage = pages.length > 1 && (lastPage?.isFetching ?? false)

  return (
    <main data-testid={`screen-sessions-${scope}`}>
      <PageScaffold>
        <PageHeader
          actions={
            scope === 'upcoming' ? (
              <Link
                className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
                to="/sessions/new"
              >
                {t('create.heading')}
              </Link>
            ) : undefined
          }
          title={headingText}
        />

        {isPending && (
          <p role="status" className="motion-safe:animate-pulse text-ink-soft">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}

        {error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
              defaultValue: t('errors.generic'),
            })}
          </StatusBanner>
        )}

        {!isPending && items.length === 0 && (
          <EmptyState
            action={
              <Link
                className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
                to="/sessions/new"
              >
                {t('emptyState.sessions.cta')}
              </Link>
            }
            illustration="🎲"
            title={emptyText}
          >
            {t('emptyState.sessions.body')}
          </EmptyState>
        )}

        {items.length > 0 && (
          <Section label={headingText}>
            <ul>
              {items.map((session) => {
                const inProgress = scope === 'upcoming' && isSessionInProgress(session)
                return (
                  <li key={session.id} className="border-t border-ink/25 first:border-t-0">
                    <Link
                      className="flex flex-col gap-1 rounded-control px-2 py-3 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                      to={`/sessions/${session.id}`}
                    >
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="font-display font-semibold text-ink">
                          {session.title}
                        </span>
                        <SessionStatusChip status={session.status} />
                        {inProgress && (
                          <span className={chipClasses}>{t('list.inProgress')}</span>
                        )}
                      </span>
                      <span className="flex flex-wrap items-center gap-2">
                        <span className={quietChipClasses}>
                          {new Intl.DateTimeFormat(i18n.language, {
                            dateStyle: 'medium',
                            timeStyle: 'short',
                            timeZone: viewerTimeZone,
                          }).format(new Date(session.startTime))}
                        </span>
                        {session.platform && (
                          <span className={quietChipClasses}>{session.platform}</span>
                        )}
                      </span>
                    </Link>
                  </li>
                )
              })}
            </ul>
          </Section>
        )}

        {nextCursor && (
          <Button
            aria-busy={isFetchingNextPage}
            disabled={isFetchingNextPage}
            variant="neutral"
            onClick={() => setCursors((current) => [...current, nextCursor])}
          >
            {t('list.loadMore')}
          </Button>
        )}
      </PageScaffold>
    </main>
  )
}
