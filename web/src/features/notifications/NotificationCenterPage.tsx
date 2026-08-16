import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bell } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  neutralCtaClasses,
  segmentItemClasses,
  segmentStripClasses,
} from '../../components/ui/classes'
import { EmptyState } from '../../components/shell/SectionCard'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold, PageSlot } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { useToast } from '../../components/ui/toastStore'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
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
    refetch: () => pages.forEach((page) => void page.refetch()),
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
  const namesAPerson = FRIEND_TYPES.has(notification.type)
  const detail = namesAPerson
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
    <li
      className="border-t border-ink/25 first:border-t-0"
      data-testid={`notification-${notification.id}`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3 px-2 py-3">
        <div className="flex min-w-0 items-start gap-2">
          {namesAPerson && (
            <AvatarDisc
              className="notification-actor-disc"
              emoji={notification.actorAvatar?.emoji}
              imgUrl={avatarImgUrl(notification.actorAvatar)}
              masked={notification.actorDisplayName === null}
              seed={notification.actorUserId ?? notification.id}
              size="xs"
            />
          )}
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
  const [scope, setScope] = useState<NotificationScope>('all')
  const queryClient = useQueryClient()
  const toast = useToast()
  const { items, isPending, error, nextCursor, isFetchingNextPage, loadMore, refetch } =
    useNotificationScope(scope)

  // Ruling 1: opening the center is a read-only GET — neither an effect nor
  // render ever calls a mark-read mutation. Only the explicit controls below
  // do. The toast is additive visual chrome (§3.5) on top of the shipped
  // strings; it carries no announcement duty of its own.
  const markRead = useMutation({
    mutationFn: markNotificationRead,
    onSuccess: async () => {
      toast({ message: t('toastCopy.markedRead') })
      await invalidateNotifications(queryClient)
    },
  })

  const markAllRead = useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: async () => {
      toast({ message: t('toastCopy.markedAllRead') })
      await invalidateNotifications(queryClient)
    },
  })

  // Gated on the unread-count query, not the loaded page (F2): a user whose
  // unread rows are all beyond page 1 must still see an enabled control —
  // `items.some(unread)` only reflects what happened to be paged in.
  const unreadCountQuery = useQuery({
    queryKey: notificationKeys.unreadCount,
    queryFn: getUnreadNotificationCount,
  })
  // A failed count fetch falls OPEN, not closed: "Mark all read" staying
  // enabled on an unknown count is recoverable (a no-op reload at worst);
  // falling closed would silently hide real unread work behind a
  // legitimate-looking disabled control.
  const hasUnread = unreadCountQuery.isError || (unreadCountQuery.data ?? 0) > 0
  const anyError = error ?? unreadCountQuery.error

  return (
    <main data-testid="screen-notifications">
      <PageScaffold>
        <PageHeader
          actions={
            <Button
              aria-busy={markAllRead.isPending}
              disabled={markAllRead.isPending || !hasUnread}
              variant="neutral"
              onClick={() => markAllRead.mutate()}
            >
              {t('actions.markAllRead')}
            </Button>
          }
          title={t('list.heading')}
        />
        <PageSlot />

        {/* SegmentStrip skin (§3.2) on the shipped tablist semantics — the
            roles, accessible names and handler are untouched. */}
        <div aria-label={t('nav.label')} className={segmentStripClasses} role="tablist">
          {(['all', 'unread'] as const).map((option) => (
            <button
              key={option}
              aria-selected={scope === option}
              className={segmentItemClasses(scope === option)}
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

        {anyError && (
          <StatusBanner
            actions={
              <button
                className={neutralCtaClasses}
                type="button"
                onClick={() => {
                  refetch()
                  void unreadCountQuery.refetch()
                }}
              >
                {t('actions.retry', { ns: 'common' })}
              </button>
            }
            role="alert"
            variant="danger"
          >
            {t(isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic', {
              defaultValue: t('errors.generic'),
            })}
          </StatusBanner>
        )}

        {/* An empty inbox is a good terminal state, so it is quiet: no CTA. */}
        {!isPending && items.length === 0 && !error && (
          <EmptyState icon={Bell} title={t(`list.empty.${scope}`)} />
        )}

        {items.length > 0 && (
          <Section label={t('list.heading')}>
            <ul>
              {items.map((item) => (
                <NotificationRow
                  key={item.id}
                  isMarking={markRead.isPending && markRead.variables === item.id}
                  notification={item}
                  onMarkRead={(id) => markRead.mutate(id)}
                />
              ))}
            </ul>
          </Section>
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
      </PageScaffold>
    </main>
  )
}
