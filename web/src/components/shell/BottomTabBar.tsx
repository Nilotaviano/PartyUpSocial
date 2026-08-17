import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router'
import { NAV_ITEMS, isNavItemActive } from './navItems'

/**
 * Mobile section navigation. Six equal targets (>=44px), active state
 * carried by an indicator chip + bold label, never colour alone.
 *
 * `Link`, not `NavLink` — see the same note in `./TopBar`.
 */
export function BottomTabBar() {
  const { t } = useTranslation(['common', 'sessions', 'polls', 'availability', 'groups'])
  const { pathname } = useLocation()

  return (
    <nav
      aria-label={t('navigation.tabBarLabel')}
      className="fixed inset-x-0 bottom-0 z-40 border-t border-border bg-card/95 pb-[env(safe-area-inset-bottom)] backdrop-blur lg:hidden"
    >
      <ul className="mx-auto grid max-w-[600px] grid-cols-6">
        {NAV_ITEMS.map((item) => {
          const active = isNavItemActive(pathname, item)
          return (
            <li key={item.to} className="min-w-0">
              <Link
                aria-current={active ? 'page' : undefined}
                className={[
                  'flex h-16 flex-col items-center justify-center gap-1 px-1 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
                  active ? 'text-foreground' : 'text-muted-foreground',
                ].join(' ')}
                data-tour={item.tourId}
                to={item.to}
              >
                <span
                  className={[
                    'grid h-7 w-12 place-items-center rounded-full transition-colors',
                    active ? 'bg-primary/25 ring-1 ring-primary' : '',
                  ].join(' ')}
                >
                  <item.icon aria-hidden className="size-[18px]" />
                </span>
                <span
                  className={[
                    'w-full truncate text-center font-display text-[11px] font-bold',
                    active ? 'font-extrabold' : '',
                  ].join(' ')}
                >
                  {t(item.labelKey, { ns: item.ns })}
                </span>
              </Link>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}
