import { useQuery } from '@tanstack/react-query'
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
        className={({ isActive }) =>
          `relative inline-flex min-h-11 flex-none items-center rounded-control px-3 py-2 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
            isActive
              ? 'bg-primary font-semibold text-primary-ink'
              : 'text-ink-soft hover:bg-accent hover:text-accent-ink'
          }`
        }
        data-tour="notifications"
        to="/notifications"
      >
        <svg aria-hidden="true" className="size-6" fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} viewBox="0 0 24 24">
          <path d="M6 8a6 6 0 0 1 12 0c0 4 1.5 6 2 6.5H4c.5-.5 2-2.5 2-6.5Z" />
          <path d="M10 18.5a2 2 0 0 0 4 0" />
        </svg>
        {count > 0 && (
          <span
            aria-hidden="true"
            className="absolute right-0 top-0 inline-flex min-w-5 items-center justify-center rounded-full border-2 border-ink bg-accent px-1 font-display text-xs font-bold leading-none text-accent-ink"
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
