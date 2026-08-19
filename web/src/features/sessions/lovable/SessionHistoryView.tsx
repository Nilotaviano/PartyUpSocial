import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { History, SearchX } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { Button } from '../../../components/ui/button'
import { primaryCtaClasses } from '../../../components/ui/classes'
import { EmptyState, SectionCard } from '../../../components/shell/SectionCard'
import { getSessionHistoryStats, sessionKeys } from '../sessionQueries'
import { HistoryEntry, HistoryFilters, HistoryRecap, HistorySkeleton } from './HistoryBits'
import type { SessionSummaryResponse } from './sessionData'
import {
  emptyHistoryFilter,
  filterHistory,
  gameOptions,
  groupByMonth,
  mapHistoryEntry,
  playerOptions,
  type HistoryFilter,
} from './historyData'

/**
 * Sessions > History (D-221/D-222/D-223): the viewer's own record of nights
 * already played. Paging stays with `SessionListPage` (the shared cursor
 * idiom); this view owns the D-222 recap query and the D-223 client-side
 * narrowing over whatever pages are loaded.
 */
export function SessionHistoryView({
  items,
  isPending,
  timeZone,
  viewerId,
  viewerName,
  loadMore,
}: {
  items: SessionSummaryResponse[]
  isPending: boolean
  timeZone: string
  viewerId?: string | null
  viewerName?: string | null
  loadMore?: React.ReactNode
}) {
  const { t, i18n } = useTranslation('sessions')
  const [filter, setFilter] = useState<HistoryFilter>(emptyHistoryFilter)

  const stats = useQuery({
    queryKey: sessionKeys.historyStats(),
    queryFn: getSessionHistoryStats,
  })

  const entries = useMemo(
    () => items.map((item) => mapHistoryEntry(item, timeZone, viewerId)),
    [items, timeZone, viewerId],
  )
  const results = useMemo(() => filterHistory(entries, filter), [entries, filter])
  const months = useMemo(() => groupByMonth(results), [results])
  const games = useMemo(() => gameOptions(entries), [entries])
  const players = useMemo(() => playerOptions(entries, viewerName), [entries, viewerName])

  const monthLabel = (monthStart: Date) =>
    new Intl.DateTimeFormat(i18n.language, {
      month: 'long',
      year: 'numeric',
      timeZone: 'UTC',
    }).format(monthStart)

  if (isPending) return <HistorySkeleton />

  if (entries.length === 0) {
    return (
      <EmptyState
        action={
          <Link className={primaryCtaClasses} to="/sessions/new">
            {t('history.empty.cta')}
          </Link>
        }
        description={t('history.empty.body')}
        icon={History}
        title={t('history.empty.title')}
      />
    )
  }

  return (
    <div className="space-y-4">
      {stats.data && <HistoryRecap stats={stats.data} />}

      <HistoryFilters
        filter={filter}
        games={games}
        players={players}
        resultCount={results.length}
        onChange={setFilter}
      />

      {months.length === 0 ? (
        <EmptyState
          action={
            <Button variant="neutral" onClick={() => setFilter(emptyHistoryFilter)}>
              {t('history.filters.clear')}
            </Button>
          }
          description={t('history.noMatches.body')}
          icon={SearchX}
          title={t('history.noMatches.title')}
        />
      ) : (
        months.map((month) => (
          <SectionCard
            action={
              <span className="shrink-0 text-meta text-ink-soft tabular-nums">
                {t('history.month.count', { count: month.items.length })}
              </span>
            }
            icon={History}
            key={month.key}
            title={monthLabel(month.monthStart)}
          >
            <ul className="space-y-2">
              {month.items.map((entry) => (
                <HistoryEntry entry={entry} key={entry.id} />
              ))}
            </ul>
          </SectionCard>
        ))
      )}

      {loadMore}
    </div>
  )
}
