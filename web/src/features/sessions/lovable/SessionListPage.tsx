import { useQueries, useQuery } from '@tanstack/react-query'
import { CalendarClock, Dices } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import { raisedInteractiveClasses } from '../../../components/ui/classes'
import { EmptyState } from '../../../components/shell/SectionCard'
import { PageHeader } from '../../../components/shell/PageHeader'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { cn } from '../../availability/lovable/utils'
import {
  getSessionList,
  getViewerProfile,
  isSessionInProgress,
  sessionKeys,
  viewerProfileKey,
  type SessionScope,
  type SessionSummary,
} from '../sessionQueries'
import { OccupancyChip, OrganizerRow, StatusPill } from './SessionBits'
import {
  isKnownSessionStatus,
  mapSessionSummary,
  sessionPlatformLabel,
  type SessionLabels,
} from './sessionData'

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

function SessionCard({
  session,
  scope,
  timeZone,
  labels,
}: {
  session: SessionSummary
  scope: SessionScope
  timeZone: string | undefined
  labels: SessionLabels
}) {
  const { t, i18n } = useTranslation('sessions')
  const mapped = mapSessionSummary(session, timeZone ?? 'UTC', labels)
  const inProgress = scope === 'upcoming' && isSessionInProgress(session)
  const dimmed = mapped.status === 'canceled' || mapped.status === 'expired'

  const startText = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(mapped.startTime)

  const occupancyLabel =
    mapped.maxCapacity == null
      ? t('list.occupancyUnlimited', { active: mapped.activeOccupancy })
      : t('list.occupancy', { active: mapped.activeOccupancy, max: mapped.maxCapacity })

  return (
    <li>
      <Link
        className={cn(
          'surface block h-full p-4 transition-colors hover:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
          dimmed && 'opacity-80',
        )}
        to={`/sessions/${session.id}`}
      >
        <div className="flex items-start justify-between gap-2">
          <h3 className="min-w-0 truncate font-display text-base font-extrabold text-ink">
            {mapped.title}
          </h3>
          {isKnownSessionStatus(mapped.status) && (
            <StatusPill status={mapped.status} label={t(`status.${mapped.status}`)} />
          )}
        </div>

        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="inline-flex items-center gap-2 rounded-xl border border-border bg-muted/50 px-2.5 py-2">
            <CalendarClock className="size-4 shrink-0" />
            <span className="min-w-0 truncate text-[12px] font-semibold tabular-nums">
              {startText}
            </span>
          </span>
          {inProgress && (
            <span className="rounded-full border-2 border-ink bg-accent px-2.5 py-1 font-display text-[11px] font-bold text-accent-ink">
              {t('list.inProgress')}
            </span>
          )}
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
          {mapped.game && <span>{mapped.game.name}</span>}
          <span>{sessionPlatformLabel(mapped.platform, t)}</span>
        </div>

        <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
          <OrganizerRow organizer={mapped.organizer} organizerLabel={t('detail.organizer')} />
          <OccupancyChip label={occupancyLabel} />
        </div>
      </Link>
    </li>
  )
}

type SessionListPageProps = {
  scope: SessionScope
}

export function SessionListPage({ scope }: SessionListPageProps) {
  const { t } = useTranslation(['sessions', 'errors', 'common'])
  const labels: SessionLabels = { anonymousOrganizer: t('detail.anonymous') }

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

  return (
    <main className="min-h-screen bg-background pb-24" data-testid={`screen-sessions-${scope}`}>
      <div className="mx-auto max-w-[1000px] px-3 pb-6 sm:px-6">
        <PageHeader
          actions={
            scope === 'upcoming' && (
              <Link
                className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
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
                  className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
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
                <SessionCard
                  key={session.id}
                  labels={labels}
                  scope={scope}
                  session={session}
                  timeZone={viewerTimeZone}
                />
              ))}
            </ul>
          )}

          {nextCursor && (
            <Button
              aria-busy={isFetchingNextPage}
              className="self-start"
              disabled={isFetchingNextPage}
              variant="neutral"
              onClick={loadMore}
            >
              {t('list.loadMore')}
            </Button>
          )}
        </div>
      </div>
    </main>
  )
}
