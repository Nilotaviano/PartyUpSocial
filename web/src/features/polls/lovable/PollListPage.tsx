import { useQueries } from '@tanstack/react-query'
import { CalendarClock, Plus, Vote as VoteIcon } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import {
  raisedInteractiveClasses,
  segmentItemClasses,
  segmentStripClasses,
} from '../../../components/ui/classes'
import { EmptyState } from '../../../components/ui/emptyState'
import { useToast } from '../../../components/ui/toastStore'
import { cn } from '../../availability/lovable/utils'
import { getPollList, pollKeys, pollWindowState, type PollBand, type PollSummary } from '../pollQueries'
import { StatusPill } from './PollBits'
import type { PollStatus } from './pollData'

/**
 * Ported from the Lovable prototype's `src/routes/polls.index.tsx` (114
 * lines), byte-faithful to the card grid / StatusPill treatment apart from:
 *  - The prototype's `AppHeader` is dropped — the repo's authenticated shell
 *    already renders navigation around every route (S2's write set excludes
 *    `App.tsx`, which is where the route will be wired in a later slice).
 *  - The prototype's mock `POLLS` fixture (with per-poll `game`/`platform`/
 *    `finalizedOptionId`/`voterIds` and a full options array) is replaced by
 *    the real, independently-cursored `usePollBand` hook against
 *    `GET /api/v1/polls` — `PollSummaryResponse` only carries `optionCount`/
 *    `createdAt`, so the card's second/third rows show those facts instead
 *    of the prototype's "Leading/Locked in" option preview and
 *    available/maybe tally (no per-option data ships on the list endpoint;
 *    that only exists on `PollDetailResponse`, rendered by the detail page).
 *  - The prototype only ever rendered one list. The real page still needs
 *    both the involved (§7 rule-1) and discoverable (D-118) bands, each with
 *    its own cursor chain (mirrors the old `PollListPage`'s `usePollBand`),
 *    so this skin adds a `SegmentStrip` tablist switching between them —
 *    the same tab idiom `NotificationCenterPage` already uses for its own
 *    two independently-cursored scopes, "adjust state during render" reset
 *    included, so switching bands starts a fresh cursor chain rather than
 *    mixing pages minted under a different band.
 *  - Errors surface through the toast queue (`useToast`), the same
 *    convention `CalendarShell` (the calendar port) uses for its own query
 *    errors, rather than the flat page's inline `StatusBanner`.
 *  - `StatusPill` (from S1's `PollBits`) needs an icon-bearing status kind
 *    plus an already-resolved label string; the windowState-to-label mapping
 *    duplicates the old page's `PollStatusChip` (still shipping unmodified
 *    for the current route) rather than importing that component, since it
 *    renders a plain `chipClasses` span, not the prototype's icon pill.
 */

function usePollBand(band: PollBand) {
  // "Adjust state during render" (matches `NotificationCenterPage`'s
  // `useNotificationScope`): switching bands starts a fresh cursor chain
  // synchronously, before the stale chain would otherwise paint.
  const [activeBand, setActiveBand] = useState(band)
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  if (band !== activeBand) {
    setActiveBand(band)
    setCursors([undefined])
  }

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

/** Same clock-inclusive gate `PollStatusChip` reads (design §5/§6.1); mapped onto
 * S1's `PollStatus` icon/colour kinds — `awaitingSelection` borrows the `closed`
 * pill (Clock icon), `expiredPending` borrows the `expired` pill (CircleSlash
 * icon), since neither is its own wire status. */
function pillStatusFor(poll: PollSummary): PollStatus {
  if (poll.status === 'finalized' || poll.status === 'expired' || poll.status === 'canceled') {
    return poll.status
  }
  const windowState = pollWindowState(poll)
  if (windowState === 'expiredPending') return 'expired'
  if (windowState === 'awaitingSelection') return 'closed'
  return 'polling'
}

function statusLabelFor(
  t: (key: string, options?: Record<string, unknown>) => string,
  poll: PollSummary,
  deadlineText: string,
): string {
  if (poll.status === 'finalized') return t('status.finalized')
  if (poll.status === 'expired') return t('status.expired')
  if (poll.status === 'canceled') return t('status.canceled')
  const windowState = pollWindowState(poll)
  if (windowState === 'expiredPending') return t('status.expiredPending')
  if (windowState === 'awaitingSelection') return t('status.awaitingSelection')
  if (windowState === 'polling') return t('status.pollingUntil', { deadline: deadlineText })
  return t('status.closed')
}

function PollCard({ poll }: { poll: PollSummary }) {
  const { t, i18n } = useTranslation('polls')
  const formatter = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
  const deadlineText = formatter.format(new Date(poll.deadline))
  const createdText = formatter.format(new Date(poll.createdAt))
  const dimmed = poll.status === 'canceled' || poll.status === 'expired'

  return (
    <li>
      <Link
        className={cn(
          'surface block h-full p-4 transition-colors hover:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
          dimmed && 'opacity-80',
        )}
        to={`/polls/${poll.id}`}
      >
        <div className="flex items-start justify-between gap-2">
          <h3 className="min-w-0 truncate font-display text-base font-extrabold text-ink">
            {poll.title}
          </h3>
          <StatusPill status={pillStatusFor(poll)} label={statusLabelFor(t, poll, deadlineText)} />
        </div>

        <div className="mt-3 flex items-center gap-2 rounded-xl border border-border bg-muted/50 px-2.5 py-2">
          <CalendarClock className="size-4 shrink-0" />
          <span className="min-w-0 truncate text-[12px] font-semibold tabular-nums">
            {createdText}
          </span>
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
          <span className="inline-flex items-center gap-1">
            <VoteIcon className="size-3.5" />
            {t('list.optionCount', { count: Number(poll.optionCount) })}
          </span>
        </div>
      </Link>
    </li>
  )
}

const BANDS = ['involved', 'discoverable'] as const

export function PollListPage() {
  const { t } = useTranslation(['polls', 'common'])
  const showToast = useToast()
  const [band, setBand] = useState<PollBand>('involved')
  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore } = usePollBand(band)

  useEffect(() => {
    if (!error) return
    showToast({
      message: isApiError(error)
        ? t(`errors.codes.${error.code}`, { defaultValue: t('errors.generic') })
        : t('errors.generic'),
    })
  }, [error, showToast, t])

  const emptyText = band === 'involved' ? t('list.emptyInvolved') : t('list.emptyDiscoverable')

  return (
    <main className="min-h-screen bg-background pb-24" data-testid="screen-polls">
      <div className="mx-auto max-w-[1000px] px-3 py-6 sm:px-6">
        <div className="surface paper-grain flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="min-w-0">
            <h1 className="font-display text-lg font-extrabold text-ink">{t('list.heading')}</h1>
            <p className="text-[12px] text-ink-soft">{t('list.description')}</p>
          </div>
          <Link
            className={`inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-ink ${raisedInteractiveClasses}`}
            to="/polls/new"
          >
            <Plus aria-hidden="true" className="size-4" />
            {t('create.heading')}
          </Link>
        </div>

        <div aria-label={t('list.bandTabsLabel')} className={`mt-4 ${segmentStripClasses}`} role="tablist">
          {BANDS.map((option) => (
            <button
              key={option}
              aria-controls={`polls-panel-${option}`}
              aria-selected={band === option}
              className={segmentItemClasses(band === option)}
              id={`polls-tab-${option}`}
              role="tab"
              type="button"
              onClick={() => setBand(option)}
            >
              {option === 'involved' ? t('list.involved') : t('list.discoverable')}
            </button>
          ))}
        </div>

        <div
          aria-labelledby={`polls-tab-${band}`}
          className="mt-4 flex flex-col gap-3"
          id={`polls-panel-${band}`}
          role="tabpanel"
        >
          {isPending && (
            <p className="motion-safe:animate-pulse text-ink-soft" role="status">
              {t('common.loading', { ns: 'common' })}
            </p>
          )}

          {/*
            The discoverable band's empty is a good terminal state — nothing is wrong when
            nobody else has an open poll — so it stays quiet; only the viewer's own band
            offers the way out (§3.6), same as the old page.
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
            <ul className="grid gap-3 sm:grid-cols-2">
              {items.map((poll) => (
                <PollCard key={poll.id} poll={poll} />
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
