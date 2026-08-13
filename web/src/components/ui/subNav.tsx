import { NavLink } from 'react-router'

/**
 * Within-area navigation (STYLING_OVERHAUL_DESIGN.md §3.1): a link list in the
 * shipped active-pill styling, sitting below the PageHeader. `NavLink` owns
 * `aria-current="page"` — the state is never colour alone, since the active
 * pill is also the only filled one and carries the current-page semantics.
 */
export interface SubNavItem {
  to: string
  label: string
}

export interface SubNavProps {
  label: string
  items: readonly SubNavItem[]
  className?: string
}

export function SubNav({ label, items, className }: SubNavProps) {
  const classes = ['-mt-2', className].filter(Boolean).join(' ')

  return (
    <nav aria-label={label} className={classes}>
      <ul className="flex flex-wrap gap-2">
        {items.map((item) => (
          <li key={item.to}>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 items-center rounded-full border-2 px-4 py-2 font-display font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'border-ink bg-accent text-accent-ink dark:border-accent-ink'
                    : 'border-transparent text-ink-soft hover:border-ink/25'
                }`
              }
              end
              to={item.to}
            >
              {item.label}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  )
}
