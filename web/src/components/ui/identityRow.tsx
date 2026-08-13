import type { ReactNode } from 'react'

/**
 * The one way a person is rendered (STYLING_OVERHAUL_DESIGN.md §3.3).
 *
 * - `full` — avatar + display name + an *optional* `@username`. Rosters and
 *   voters carry no username on the wire, so the slot renders nothing at all
 *   when absent: no placeholder, no reserved gap.
 * - `handle` — username only. D-068/D-188 keep request cards username-only, so
 *   this variant refuses `displayName`/`avatar` at the type level: leaking a
 *   display name onto a request card is a compile error, not a review catch.
 * - `masked` — the D-076 "Anonymous User" row: the busy pair and ordinary type,
 *   deliberately indistinguishable in layout from an unmasked row.
 *
 * The avatar is a `ReactNode` slot rather than an import: `AvatarDisc` lives in
 * `features/friends`, and a `components/ui` primitive reaching into a feature
 * would invert the dependency direction every other primitive here respects.
 * Callers pass `<AvatarDisc seed={userId} … />` (and `masked` on masked rows).
 */
interface IdentityRowBaseProps {
  meta?: ReactNode
  actions?: ReactNode
  /**
   * The element the person's name renders as. A row that *is* the subject of the
   * surface it sits on (a profile card, a Steam match card) names a heading; a
   * repeated row in a list names a `span`, which is the default.
   */
  nameAs?: 'span' | 'h2' | 'h3'
  className?: string
  'data-testid'?: string
}

export type IdentityRowProps = IdentityRowBaseProps &
  (
    | {
        variant?: 'full'
        displayName: ReactNode
        username?: string | null
        avatar?: ReactNode
      }
    | { variant: 'handle'; username: string; displayName?: never; avatar?: never }
    | {
        variant: 'masked'
        displayName: ReactNode
        avatar?: ReactNode
        username?: never
      }
  )

const handleClasses = 'block truncate font-mono text-meta text-ink-soft'

export function IdentityRow({
  meta,
  actions,
  nameAs: Name = 'span',
  className,
  'data-testid': testId,
  ...identity
}: IdentityRowProps) {
  const classes = ['flex min-w-0 items-center gap-3', className].filter(Boolean).join(' ')
  const avatar = identity.variant === 'handle' ? undefined : identity.avatar

  return (
    <div className={classes} data-testid={testId}>
      {avatar}
      <div className="flex min-w-0 flex-1 flex-col">
        {identity.variant === 'handle' ? (
          <Name className="block truncate font-mono text-body font-semibold text-ink">
            @{identity.username}
          </Name>
        ) : (
          <>
            <Name className="block truncate font-display font-semibold text-ink">
              {identity.displayName}
            </Name>
            {identity.variant !== 'masked' && identity.username ? (
              <span className={handleClasses}>@{identity.username}</span>
            ) : null}
          </>
        )}
        {meta && <span className="block truncate font-body text-meta text-ink-soft">{meta}</span>}
      </div>
      {actions && <div className="flex flex-none items-center gap-2">{actions}</div>}
    </div>
  )
}
