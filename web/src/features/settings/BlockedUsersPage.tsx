import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { NavLink } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  friendKeys,
  getBlockedUsers,
  invalidateFriendGraph,
  runFriendGraphAction,
} from '../friends/friendGraph'

export function SettingsNavigation() {
  const { t } = useTranslation('settings')
  const links = [
    ['/settings/profile', t('navigation.profile')],
    ['/settings/account', t('navigation.account')],
    ['/settings/blocked', t('navigation.blocked')],
    ['/settings/notifications', t('navigation.notifications')],
  ] as const

  return (
    <nav aria-label={t('navigation.label')} className="mb-6">
      <ul className="flex flex-wrap gap-2">
        {links.map(([to, label]) => (
          <li key={to}>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 items-center rounded-full border-2 px-4 py-2 font-display font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${
                  isActive
                    ? 'border-ink bg-accent text-accent-ink dark:border-accent-ink'
                    : 'border-transparent text-ink-soft'
                }`
              }
              end
              to={to}
            >
              {label}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  )
}

export function BlockedUsersPage() {
  const { t, i18n } = useTranslation(['friends', 'errors'])
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const blocks = useQuery({
    queryKey: friendKeys.blocks,
    queryFn: getBlockedUsers,
  })
  const unblock = useMutation({
    mutationFn: runFriendGraphAction,
    onSuccess: async (_result, input) => {
      setStatus(t('status.unblocked'))
      await invalidateFriendGraph(queryClient, input)
    },
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  return (
    <main
      className="mx-auto max-w-4xl p-4 sm:p-8"
      data-testid="screen-settings-blocked"
    >
      <SettingsNavigation />
      <h1
        ref={heading}
        tabIndex={-1}
        className="font-display text-display font-bold text-ink focus:outline-none"
      >
        {t('pages.blocked')}
      </h1>
      <p className="mt-2 text-ink-soft">{t('empty.blockedHelp')}</p>
      <p aria-live="polite" className="mt-3 text-ink">
        {status}
      </p>

      {blocks.isPending && (
        <p
          aria-hidden="true"
          className="mt-6 motion-safe:animate-pulse text-ink-soft"
        >
          {t('status.loading')}
        </p>
      )}
      {blocks.error && (
        <p className="mt-6" role="alert">
          {t(isApiError(blocks.error) ? blocks.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
      {blocks.data?.length === 0 && (
        <p className="mt-6 text-ink-soft">{t('empty.blocked')}</p>
      )}
      {blocks.data && blocks.data.length > 0 && (
        <ul className="mt-6 grid gap-3 sm:grid-cols-2">
          {blocks.data.map((blocked) => {
            const date = new Intl.DateTimeFormat(i18n.language, {
              dateStyle: 'medium',
            }).format(new Date(blocked.createdAt))
            return (
              <li
                key={blocked.blockId}
                className="flex flex-col items-start justify-between gap-4 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card sm:flex-row sm:items-center"
              >
                <div className="min-w-0">
                  <p className="truncate font-mono font-semibold text-ink">
                    @{blocked.username}
                  </p>
                  <p className="text-meta text-ink-soft">
                    {t('labels.blockedOn', { date })}
                  </p>
                </div>
                <Button
                  aria-busy={unblock.isPending}
                  aria-label={`${t('actions.unblock')} @${blocked.username}`}
                  className="shrink-0"
                  disabled={unblock.isPending}
                  variant="neutral"
                  onClick={() =>
                    unblock.mutate({
                      kind: 'unblock',
                      username: blocked.username,
                      blockId: blocked.blockId,
                    })
                  }
                >
                  {t('actions.unblock')}
                </Button>
              </li>
            )
          })}
        </ul>
      )}
      {unblock.error && (
        <p
          ref={(node) => node?.focus()}
          className="mt-4"
          role="alert"
          tabIndex={-1}
        >
          {t(isApiError(unblock.error) ? unblock.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
    </main>
  )
}
