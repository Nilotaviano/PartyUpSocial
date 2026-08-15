import { useQuery } from '@tanstack/react-query'
import { Bell } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { NavLink } from 'react-router'
import { getUnreadNotificationCount, notificationKeys } from './notificationQueries'

const OVERFLOW_CEILING = 99

/**
 * Design §5: exact count, `99+` above 99 (ruling 1). The default
 * `QueryClient` already refetches on window focus; mark-read/mark-all
 * mutations refetch this same query via `invalidateNotifications`
 * (`['notifications','unread-count']`), so no bespoke focus wiring lives
 * here.
 */
export function NotificationBell() {
  const { t } = useTranslation('notifications')
  const { data: count = 0 } = useQuery({
    queryKey: notificationKeys.unreadCount,
    queryFn: getUnreadNotificationCount,
  })

  const badgeText = count > OVERFLOW_CEILING ? t('bell.badgeOverflow') : String(count)
  const label = count > 0 ? t('bell.labelUnread', { count }) : t('bell.label')

  return (
    <>
      <NavLink
        aria-label={label}
        className="relative inline-flex size-11 items-center justify-center rounded-xl text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        data-tour="notifications"
        to="/notifications"
      >
        <Bell aria-hidden className="size-5" />
        {count > 0 && (
          <span
            aria-hidden="true"
            className="absolute right-1.5 top-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-0.5 text-[9px] font-bold text-destructive-foreground tabular-nums"
          >
            {badgeText}
          </span>
        )}
      </NavLink>
      <p aria-live="polite" className="sr-only" role="status">
        {count > 0 ? t('bell.announcement', { count }) : ''}
      </p>
    </>
  )
}
