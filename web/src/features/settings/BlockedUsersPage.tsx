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
  ] as const

  return (
    <nav aria-label={t('navigation.label')} className="mb-6">
      <ul className="flex flex-wrap gap-2">
        {links.map(([to, label]) => (
          <li key={to}>
            <NavLink
              className={({ isActive }) =>
                `inline-flex min-h-11 items-center rounded-md px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 ${
                  isActive
                    ? 'bg-indigo-100 font-semibold text-indigo-950'
                    : 'text-slate-700 hover:bg-slate-100'
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
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {t('pages.blocked')}
      </h1>
      <p className="mt-2 text-slate-700">{t('empty.blockedHelp')}</p>
      <p aria-live="polite" className="mt-3 text-sm text-emerald-700">
        {status}
      </p>

      {blocks.isPending && (
        <p
          aria-hidden="true"
          className="mt-6 motion-safe:animate-pulse text-slate-500"
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
        <p className="mt-6">{t('empty.blocked')}</p>
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
                className="flex flex-col items-start justify-between gap-4 rounded-xl sm:flex-row sm:items-center border border-slate-200 bg-white p-4 shadow-sm"
              >
                <div className="min-w-0">
                  <p className="truncate font-mono font-semibold text-slate-950">
                    @{blocked.username}
                  </p>
                  <p className="text-sm text-slate-600">
                    {t('labels.blockedOn', { date })}
                  </p>
                </div>
                <Button
                  aria-busy={unblock.isPending}
                  aria-label={`${t('actions.unblock')} @${blocked.username}`}
                  className="min-h-11 shrink-0 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
                  disabled={unblock.isPending}
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
