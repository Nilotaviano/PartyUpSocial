import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  FRIEND_TYPES,
  getNotificationList,
  getUnreadNotificationCount,
  invalidateNotifications,
  markAllNotificationsRead,
  markNotificationRead,
  notificationHref,
  notificationKeys,
  type NotificationItem,
  type NotificationScope,
} from './notificationQueries'

// Design §2: `OptionStartTime` is projected for these two types only.
const OPTION_TIME_TYPES = new Set(['pollOptionSuggested', 'pollOptionSelected'])

/**
 * Design §5: two independently-cursored A-072 scopes, mirroring
 * `PollListPage`'s `usePollBand` idiom — switching scopes starts a fresh
 * cursor chain rather than mixing pages minted under different scopes.
 */
function useNotificationScope(scope: NotificationScope) {
  // React's "adjusting state when a prop changes" pattern (not an effect —
  // this runs during render, before the stale-cursor chain would otherwise
  // paint) so switching scopes starts a fresh cursor chain synchronously.
  const [activeScope, setActiveScope] = useState(scope)
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  if (scope !== activeScope) {
    setActiveScope(scope)
    setCursors([undefined])
  }

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: notificationKeys.list(scope, cursor),
      queryFn: () => getNotificationList(scope, cursor),
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

function NotificationRow({
  notification,
  isMarking,
  onMarkRead,
}: {
  notification: NotificationItem
  isMarking: boolean
  onMarkRead: (id: string) => void
}) {
  const { t, i18n } = useTranslation('notifications')
  const href = notificationHref(notification)
  const isUnread = notification.unread
  const typeLabel = t(`item.types.${notification.type}`, {
    defaultValue: t('item.types.unknown'),
  })
  // Design §4/§5: friend-graph rows always name a person (real or masked);
  // session/poll rows show the subject title when it is reachable — a
  // minimal-form row (unreachable subject) has neither and renders no
  // detail line at all.
  const detail = FRIEND_TYPES.has(notification.type)
    ? (notification.actorDisplayName ?? t('item.anonymous'))
    : (notification.subjectTitle ?? null)
  const timestamp = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(notification.createdAt))
  // Design §2: the option instant renders in the recipient's own locale
  // (D-060) for types 14/19 only, and only when present — no placeholder
  // when it is absent.
  const optionTime =
    OPTION_TIME_TYPES.has(notification.type) && notification.optionStartTime
      ? new Intl.DateTimeFormat(i18n.language, {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(new Date(notification.optionStartTime))
      : null

  // PHASE5_1_DESIGN.md §3 `:53`: state identity is never color-alone — the
  // dot glyph and bold weight together carry "unread"; the accessible name
  // states it in words too, via the sr-only prefix below.
  const titleContent = (
    <span className={isUnread ? 'font-display font-bold text-ink' : 'font-display text-ink'}>
      {isUnread && <span className="sr-only">{t('item.unreadPrefix')}</span>}
      {isUnread ? ` ${typeLabel}` : typeLabel}
    </span>
  )

  return (
    <li className="rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-2">
          {isUnread && (
            <span
              aria-hidden="true"
              className="mt-2 inline-block size-2 flex-none rounded-full bg-accent"
            />
          )}
          <div className="min-w-0">
            {href ? (
              <Link
                className="rounded-control underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                to={href}
              >
                {titleContent}
              </Link>
            ) : (
              titleContent
            )}
            {detail && <p className="text-ink-soft">{detail}</p>}
            {optionTime && (
              <p className="text-ink-soft" data-testid="option-start-time">
                {optionTime}
              </p>
            )}
            <p className="text-ink-soft">{timestamp}</p>
          </div>
        </div>
        {isUnread && (
          <Button
            aria-busy={isMarking}
            aria-label={t('actions.markReadFor', { title: typeLabel })}
            disabled={isMarking}
            variant="neutral"
            onClick={() => onMarkRead(notification.id)}
          >
            {t('actions.markRead')}
          </Button>
        )}
      </div>
    </li>
  )
}

export function NotificationCenterPage() {
  const { t } = useTranslation(['notifications', 'common'])
  const heading = useRef<HTMLHeadingElement>(null)
  const [scope, setScope] = useState<NotificationScope>('all')
  const queryClient = useQueryClient()
  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore } =
    useNotificationScope(scope)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  // Ruling 1: opening the center is a read-only GET — neither this effect
  // nor render ever calls a mark-read mutation. Only the explicit controls
  // below do.
  const markRead = useMutation({
    mutationFn: markNotificationRead,
    onSuccess: () => invalidateNotifications(queryClient),
  })

  const markAllRead = useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: () => invalidateNotifications(queryClient),
  })

  // Gated on the unread-count query, not the loaded page (F2): a user whose
  // unread rows are all beyond page 1 must still see an enabled control —
  // `items.some(unread)` only reflects what happened to be paged in.
  const unreadCountQuery = useQuery({
    queryKey: notificationKeys.unreadCount,
    queryFn: getUnreadNotificationCount,
  })
  const hasUnread = (unreadCountQuery.data ?? 0) > 0

  return (
    <main
      className="mx-auto max-w-3xl space-y-6 p-4 sm:p-8"
      data-testid="screen-notifications"
    >
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-display font-display font-bold text-ink focus:outline-none"
        >
          {t('list.heading')}
        </h1>
        <Button
          aria-busy={markAllRead.isPending}
          disabled={markAllRead.isPending || !hasUnread}
          variant="neutral"
          onClick={() => markAllRead.mutate()}
        >
          {t('actions.markAllRead')}
        </Button>
      </header>

      <div aria-label={t('nav.label')} className="flex gap-2" role="tablist">
        {(['all', 'unread'] as const).map((option) => (
          <button
            key={option}
            aria-selected={scope === option}
            className={`min-h-11 rounded-control border-2 border-ink px-4 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
              scope === option
                ? 'bg-primary font-semibold text-primary-ink'
                : 'bg-card text-ink-soft'
            }`}
            role="tab"
            type="button"
            onClick={() => setScope(option)}
          >
            {t(`list.scope.${option}`)}
          </button>
        ))}
      </div>

      {isPending && (
        <p className="motion-safe:animate-pulse text-ink-soft" role="status">
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
        <p className="text-ink-soft">{t(`list.empty.${scope}`)}</p>
      )}

      {items.length > 0 && (
        <ul className="space-y-3">
          {items.map((item) => (
            <NotificationRow
              key={item.id}
              isMarking={markRead.isPending && markRead.variables === item.id}
              notification={item}
              onMarkRead={(id) => markRead.mutate(id)}
            />
          ))}
        </ul>
      )}

      {nextCursor && (
        <Button
          aria-busy={isFetchingNextPage}
          disabled={isFetchingNextPage}
          variant="neutral"
          onClick={loadMore}
        >
          {t('list.loadMore')}
        </Button>
      )}
    </main>
  )
}
