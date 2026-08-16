import { useQueries } from '@tanstack/react-query'
import { useState } from 'react'
import { Telescope } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { primaryCtaClasses } from '../../components/ui/classes'
import { PageHeader } from '../../components/shell/PageHeader'
import { EmptyState } from '../../components/shell/SectionCard'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { getDiscoverSessions, homeKeys } from '../home/homeQueries'
import { SessionSummaryCard } from '../sessions/SessionSummaryCard'

/**
 * D-149's dedicated Discover surface — `scope=discover` on the shipped
 * `ListSessions` operation (A-072/A-077 §6.3). Every row links to the
 * shipped `/sessions/:id` detail route (D-114); Discover adds no second
 * detail surface.
 */
export function DiscoverPage() {
  const { t } = useTranslation(['home', 'sessions', 'errors', 'common'])

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
                className={primaryCtaClasses}
                to="/friends"
              >
                {t('emptyState.discover.cta')}
              </Link>
            }
            description={t('emptyState.discover.body')}
            icon={Telescope}
            title={t('discover.empty')}
          />
        )}

        {items.length > 0 && (
          <Section label={t('discover.heading')}>
            <ul className="flex flex-col gap-3">
              {items.map((session) => (
                <SessionSummaryCard key={session.id} session={session} />
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
