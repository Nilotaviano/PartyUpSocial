import { useQueries } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
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

function PollCard({ poll }: { poll: PollSummary }) {
  const { t, i18n } = useTranslation('polls')
  return (
    <li>
      <Link
        className="block rounded-card border-2 border-ink bg-card p-4 shadow-offset-card focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        to={`/polls/${poll.id}`}
      >
        <p className="flex flex-wrap items-center gap-2 font-display font-semibold text-ink">
          {poll.title}
          <PollStatusChip poll={poll} />
        </p>
        <p className="text-ink-soft">
          {t('list.optionCount', { count: Number(poll.optionCount) })}
        </p>
        <p className="text-ink-soft">
          {new Intl.DateTimeFormat(i18n.language, {
            dateStyle: 'medium',
            timeStyle: 'short',
          }).format(new Date(poll.createdAt))}
        </p>
      </Link>
    </li>
  )
}

function PollBandSection({
  band,
  headingId,
}: {
  band: PollBand
  headingId: string
}) {
  const { t } = useTranslation(['polls', 'errors', 'common'])
  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore } =
    usePollBand(band)

  const emptyText =
    band === 'involved' ? t('list.emptyInvolved') : t('list.emptyDiscoverable')

  return (
    <section aria-labelledby={headingId}>
      <h2 id={headingId} className="text-h2 font-display font-semibold text-ink">
        {band === 'involved' ? t('list.involved') : t('list.discoverable')}
      </h2>

      {isPending && (
        <p role="status" className="motion-safe:animate-pulse text-ink-soft">
          {t('common.loading', { ns: 'common' })}
        </p>
      )}

      {error && (
        <p role="alert">
          {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
            defaultValue: t('errors.generic'),
          })}
        </p>
      )}

      {!isPending && items.length === 0 && !error && (
        <p className="text-ink-soft">{emptyText}</p>
      )}

      {items.length > 0 && (
        <ul className="mt-2 space-y-3">
          {items.map((poll) => (
            <PollCard key={poll.id} poll={poll} />
          ))}
        </ul>
      )}

      {nextCursor && (
        <Button
          aria-busy={isFetchingNextPage}
          className="mt-3"
          disabled={isFetchingNextPage}
          variant="neutral"
          onClick={loadMore}
        >
          {t('list.loadMore')}
        </Button>
      )}
    </section>
  )
}

export function PollListPage() {
  const { t } = useTranslation('polls')
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  return (
    <main
      className="mx-auto max-w-4xl space-y-8 p-4 sm:p-8"
      data-testid="screen-polls"
    >
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-display font-display font-bold text-ink focus:outline-none"
        >
          {t('list.heading')}
        </h1>
        <Link
          className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
          to="/polls/new"
        >
          {t('create.heading')}
        </Link>
      </header>

      <PollBandSection band="involved" headingId="polls-involved-heading" />
      <PollBandSection band="discoverable" headingId="polls-discoverable-heading" />
    </main>
  )
}
