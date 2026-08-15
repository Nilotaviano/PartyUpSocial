import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router'
import { AccountMenu } from '../../features/account/AccountMenu'
import { NotificationBell } from '../../features/notifications/NotificationBell'
import { BrandMark } from '../ui/brandMark'
import { NAV_ITEMS, isNavItemActive } from './navItems'

/**
 * Sticky app bar. Desktop shows the full section nav; mobile keeps only the
 * brand + utilities because sections live in `BottomTabBar`. Owner ruling:
 * no theme toggle here — theme stays settings-only.
 */
// `Link`, not `NavLink`: NavLink computes its own aria-current from its route
// match and would overwrite an explicit `aria-current` prop, but Sessions'
// active range (`/sessions/*`) is wider than its link target
// (`/sessions/upcoming`) — see `isNavItemActive` in `./navItems`.
export function TopBar() {
  const { t } = useTranslation(['common', 'sessions', 'polls', 'availability'])
  const { pathname } = useLocation()

  return (
    <div className="sticky top-0 z-40 border-b border-border bg-background/85 backdrop-blur supports-[backdrop-filter]:bg-background/70">
      <div className="mx-auto grid max-w-[1400px] grid-cols-[minmax(0,1fr)_auto] items-center gap-3 px-3 py-2.5 sm:px-6">
        <Link
          aria-label={t('brand.ariaLabel')}
          className="flex min-w-0 items-center gap-2.5 rounded-xl focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          to="/app"
        >
          <span className="grid size-9 shrink-0 place-items-center rounded-xl bg-primary text-primary-foreground shadow-[var(--shadow-card)]">
            <BrandMark className="size-5" />
          </span>
          <span className="truncate font-display text-lg font-extrabold">{t('app.name')}</span>
        </Link>

        <div className="flex shrink-0 items-center gap-1.5">
          <nav aria-label={t('navigation.label')} className="mr-1 hidden items-center gap-1 lg:flex">
            {NAV_ITEMS.map((item) => {
              const active = isNavItemActive(pathname, item)
              return (
                <Link
                  key={item.to}
                  aria-current={active ? 'page' : undefined}
                  className={[
                    'inline-flex h-11 items-center gap-2 rounded-xl px-3 font-display text-[13px] font-bold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
                    active
                      ? 'bg-primary/20 text-foreground ring-1 ring-primary'
                      : 'text-muted-foreground hover:bg-muted hover:text-foreground',
                  ].join(' ')}
                  data-tour={item.tourId}
                  to={item.to}
                >
                  <item.icon aria-hidden className="size-4" />
                  {t(item.labelKey, { ns: item.ns })}
                </Link>
              )
            })}
          </nav>

          <NotificationBell />
          <AccountMenu />
        </div>
      </div>
    </div>
  )
}
