import { useQueries } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { quietChipClasses, raisedInteractiveClasses } from '../../components/ui/classes'
import { EmptyState } from '../../components/ui/emptyState'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { getDiscoverSessions, homeKeys } from '../home/homeQueries'
import { SessionStatusChip } from '../sessions/SessionStatusChip'

/**
 * D-149's dedicated Discover surface — `scope=discover` on the shipped
 * `ListSessions` operation (A-072/A-077 §6.3). Every row links to the
 * shipped `/sessions/:id` detail route (D-114); Discover adds no second
 * detail surface.
 */
export function DiscoverPage() {
  const { t, i18n } = useTranslation(['home', 'sessions', 'errors', 'common'])

  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: homeKeys.discover(cursor),
      queryFn: () => getDiscoverSessions(cursor),
    })),
  })

  const items = pages.flatMap((page) => page.data?.items ?? [])
  const isPending = pages[0]?.isPending ?? true
  const error = pages.find((page) => page.error)?.error
  const lastPage = pages[pages.length - 1]
  const nextCursor = lastPage?.data?.nextCursor ?? undefined
  const isFetchingNextPage = pages.length > 1 && (lastPage?.isFetching ?? false)

  const timeFormat = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

  return (
    <main data-testid="screen-discover">
      <PageScaffold>
        <PageHeader title={t('discover.heading')} />

        {isPending && (
          <p className="motion-safe:animate-pulse text-ink-soft" role="status">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}

        {error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
              ns: 'sessions',
              defaultValue: t('errors.generic', { ns: 'sessions' }),
            })}
          </StatusBanner>
        )}

        {!isPending && items.length === 0 && (
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
            title={t('discover.empty')}
          >
            {t('emptyState.discover.body')}
          </EmptyState>
        )}

        {items.length > 0 && (
          <Section label={t('discover.heading')}>
            <ul>
              {items.map((session) => (
                <li key={session.id} className="border-t border-ink/25 first:border-t-0">
                  <Link
                    className="flex flex-col gap-1 rounded-control px-2 py-3 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    data-viewer-relation={session.viewerRelation}
                    to={`/sessions/${session.id}`}
                  >
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
                    <span className="text-meta text-ink-soft">
                      {t('discover.organizedBy', {
                        name:
                          session.organizer.displayName ??
                          t('detail.anonymous', { ns: 'sessions' }),
                      })}
                    </span>
                  </Link>
                </li>
              ))}
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
            {t('discover.loadMore')}
          </Button>
        )}
      </PageScaffold>
    </main>
  )
}
