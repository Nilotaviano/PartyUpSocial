import { useQueries, useQuery } from '@tanstack/react-query'
import { Dices } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import { primaryCtaClasses } from '../../../components/ui/classes'
import { EmptyState } from '../../../components/shell/SectionCard'
import { PageHeader } from '../../../components/shell/PageHeader'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { SessionSummaryCard } from '../SessionSummaryCard'
import { SessionHistoryView } from './SessionHistoryView'
import {
  getSessionList,
  getViewerProfile,
  sessionKeys,
  viewerProfileKey,
  type SessionScope,
} from '../sessionQueries'

/**
 * `scope` is a prop, each scope its own route in `App.tsx`, so cursors reset
 * for free on remount rather than needing a band-switch between scopes.
 * `viewerRelation` (present on `SessionSummaryResponse`) is mapped by
 * `./sessionData` for later slices but not rendered here.
 */

function useSessionScope(scope: SessionScope) {
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: sessionKeys.list(scope, cursor),
      queryFn: () => getSessionList(scope, cursor),
    })),
  })

  const items = pages.flatMap((page) => page.data?.items ?? [])
  const isPending = pages[0]?.isPending ?? true
  const error = pages.find((page) => page.error)?.error
  const lastPage = pages[pages.length - 1]
  const nextCursor = lastPage?.data?.nextCursor ?? undefined
  const isFetchingNextPage = pages.length > 1 && (lastPage?.isFetching ?? false)

  return {
    items,
    isPending,
    error,
    nextCursor,
    isFetchingNextPage,
    loadMore: () => setCursors((current) => [...current, nextCursor as string]),
  }
}

type SessionListPageProps = {
  scope: SessionScope
}

export function SessionListPage({ scope }: SessionListPageProps) {
  const { t } = useTranslation(['sessions', 'errors', 'common'])

  // D-038/D-046: times render in the viewer's profile time zone, not the
  // browser's, falling back to the browser zone only until the profile
  // query resolves.
  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const viewerTimeZone = profile.data?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone

  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore } =
    useSessionScope(scope)

  const headingText = scope === 'upcoming' ? t('list.upcoming') : t('list.history')
  const emptyText = scope === 'upcoming' ? t('list.emptyUpcoming') : t('list.emptyHistory')

  const loadMoreButton = nextCursor ? (
    <Button
      aria-busy={isFetchingNextPage}
      className="self-start"
      disabled={isFetchingNextPage}
      variant="neutral"
      onClick={loadMore}
    >
      {t('list.loadMore')}
    </Button>
  ) : null

  if (scope === 'history') {
    return (
      <main className="min-h-screen bg-background pb-24" data-testid="screen-sessions-history">
        <div className="mx-auto max-w-[1000px] px-3 pb-6 sm:px-6">
          <PageHeader title={headingText} />
          {error ? (
            <StatusBanner role="alert" variant="danger">
              {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
                defaultValue: t('errors.generic'),
              })}
            </StatusBanner>
          ) : (
            <SessionHistoryView
              isPending={isPending}
              items={items}
              loadMore={loadMoreButton}
              timeZone={viewerTimeZone}
              viewerId={profile.data?.userId}
              viewerName={profile.data?.displayName}
            />
          )}
        </div>
      </main>
    )
  }

  return (
    <main className="min-h-screen bg-background pb-24" data-testid={`screen-sessions-${scope}`}>
      <div className="mx-auto max-w-[1000px] px-3 pb-6 sm:px-6">
        <PageHeader
          actions={
            scope === 'upcoming' && (
              <Link
                className={primaryCtaClasses}
                to="/sessions/new"
              >
                {t('create.heading')}
              </Link>
            )
          }
          title={headingText}
        />
        <div className="flex flex-col gap-3">
          {isPending && (
            <p className="motion-safe:animate-pulse text-ink-soft" role="status">
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

          {!isPending && items.length === 0 && !error && (
            <EmptyState
              action={
                <Link
                  className={primaryCtaClasses}
                  to="/sessions/new"
                >
                  {t('emptyState.sessions.cta')}
                </Link>
              }
              description={t('emptyState.sessions.body')}
              icon={Dices}
              title={emptyText}
            />
          )}

          {items.length > 0 && (
            <ul className="grid gap-3 sm:grid-cols-2">
              {items.map((session) => (
                <SessionSummaryCard
                  key={session.id}
                  session={session}
                  showInProgress={scope === 'upcoming'}
                  timeZone={viewerTimeZone}
                />
              ))}
            </ul>
          )}

          {loadMoreButton}
        </div>
      </div>
    </main>
  )
}
