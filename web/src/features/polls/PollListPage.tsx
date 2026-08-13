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
import { getPollList, pollKeys, type PollBand, type PollSummary } from './pollQueries'
import { PollStatusChip } from './PollStatusChip'

/**
 * §4.1/interpretation note 4: one route, two independently-cursored bands —
 * involved (§7 rule-1, Polling|Closed) and discoverable (non-private
 * Polling, the D-118 entry point). Each band gets its own `useQueries`
 * cursor chain, mirroring `SessionListPage`/`DiscoverPage`'s idiom.
 */
function usePollBand(band: PollBand) {
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: pollKeys.list(band, cursor),
      queryFn: () => getPollList(band, cursor),
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

function PollRow({ poll }: { poll: PollSummary }) {
  const { t, i18n } = useTranslation('polls')
  return (
    <li className="border-t border-ink/25 first:border-t-0">
      <Link
        className="flex flex-col gap-1 rounded-control px-2 py-3 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        to={`/polls/${poll.id}`}
      >
        <span className="flex flex-wrap items-center gap-2">
          <span className="font-display font-semibold text-ink">{poll.title}</span>
          <PollStatusChip poll={poll} />
        </span>
        <span className="flex flex-wrap items-center gap-2">
          <span className={quietChipClasses}>
            {t('list.optionCount', { count: Number(poll.optionCount) })}
          </span>
          <span className={quietChipClasses}>
            {new Intl.DateTimeFormat(i18n.language, {
              dateStyle: 'medium',
              timeStyle: 'short',
            }).format(new Date(poll.createdAt))}
          </span>
        </span>
      </Link>
    </li>
  )
}

function PollBandSection({ band, headingId }: { band: PollBand; headingId: string }) {
  const { t } = useTranslation(['polls', 'errors', 'common'])
  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore } =
    usePollBand(band)

  const emptyText =
    band === 'involved' ? t('list.emptyInvolved') : t('list.emptyDiscoverable')

  return (
    <Section
      title={band === 'involved' ? t('list.involved') : t('list.discoverable')}
      titleId={headingId}
    >
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

      {/*
        The discoverable band's empty is a good terminal state — nothing is wrong when
        nobody else has an open poll — so it stays quiet; only the viewer's own band
        offers the way out (§3.6).
      */}
      {!isPending && items.length === 0 && !error && band === 'involved' && (
        <EmptyState
          action={
            <Link
              className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
              to="/polls/new"
            >
              {t('emptyState.polls.cta')}
            </Link>
          }
          illustration="🗳️"
          title={emptyText}
        >
          {t('emptyState.polls.body')}
        </EmptyState>
      )}

      {!isPending && items.length === 0 && !error && band !== 'involved' && (
        <EmptyState illustration="🔭" variant="quiet">
          {emptyText}
        </EmptyState>
      )}

      {items.length > 0 && (
        <ul>
          {items.map((poll) => (
            <PollRow key={poll.id} poll={poll} />
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
    </Section>
  )
}

export function PollListPage() {
  const { t } = useTranslation('polls')

  return (
    <main data-testid="screen-polls">
      <PageScaffold>
        <PageHeader
          actions={
            <Link
              className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
              to="/polls/new"
            >
              {t('create.heading')}
            </Link>
          }
          title={t('list.heading')}
        />

        <PollBandSection band="involved" headingId="polls-involved-heading" />
        <PollBandSection band="discoverable" headingId="polls-discoverable-heading" />
      </PageScaffold>
    </main>
  )
}
