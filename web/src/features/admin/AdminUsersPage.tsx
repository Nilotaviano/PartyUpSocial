import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import {
  adminKeys,
  getAdminUsers,
  numeric,
  type AdminUserSummary,
} from './adminQueries'

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-ink-soft">{label}</dt>
      <dd className="font-display text-ink">{value}</dd>
    </div>
  )
}

function UserRow({ user }: { user: AdminUserSummary }) {
  const { t, i18n } = useTranslation('admin')
  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' })
  const number = new Intl.NumberFormat(i18n.language)
  const name = user.displayName ?? user.username ?? t('user.unknown')

  return (
    <li className="rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Link
          aria-label={t('users.open', { name })}
          className={`inline-flex min-w-0 items-center bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
          to={`/admin/users/${user.id}`}
        >
          {name}
        </Link>
        <p className="text-ink-soft">{user.username ?? t('user.unknown')}</p>
      </div>
      <dl className="mt-3 grid gap-2 sm:grid-cols-3">
        <Field label={t('user.fields.email')} value={user.email ?? t('user.unknown')} />
        <Field
          label={t('user.fields.registered')}
          value={date.format(new Date(user.created))}
        />
        <Field
          label={t('user.fields.lastActivity')}
          value={
            user.lastSeenAt === null
              ? t('user.neverActive')
              : date.format(new Date(user.lastSeenAt))
          }
        />
        <Field
          label={t('user.fields.sessionsOrganized')}
          value={number.format(numeric(user.sessionsOrganized))}
        />
        <Field
          label={t('user.fields.sessionsJoined')}
          value={number.format(numeric(user.sessionsJoined))}
        />
        <Field
          label={t('user.fields.friendCount')}
          value={number.format(numeric(user.friendCount))}
        />
        <Field
          label={t('user.fields.banState')}
          value={
            user.bannedAt === null
              ? t('user.notBanned')
              : t('user.banned', { date: date.format(new Date(user.bannedAt)) })
          }
        />
      </dl>
    </li>
  )
}

export function AdminUsersPage() {
  const { t, i18n } = useTranslation(['admin', 'common'])
  const heading = useRef<HTMLHeadingElement>(null)
  const [draft, setDraft] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const users = useQuery({
    queryKey: adminKeys.users(query, page),
    queryFn: () => getAdminUsers(query, page),
    retry: false,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  // D-177 fail-closed parity — see AdminDashboardPage's own branch.
  if (users.isError && isApiError(users.error) && users.error.status === 404) {
    return (
      <main className="mx-auto max-w-3xl p-4 sm:p-8" data-testid="screen-admin-users">
        <p className="text-ink" role="alert">
          {t('errors.codes.not_found')}
        </p>
      </main>
    )
  }

  const number = new Intl.NumberFormat(i18n.language)
  const items = users.data?.users ?? []
  const total = users.data ? numeric(users.data.total) : 0
  const pageSize = users.data ? numeric(users.data.pageSize) : 0
  const hasNextPage = users.data ? page * pageSize < total : false

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-admin-users"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('users.heading')}
      </h1>

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setQuery(draft.trim())
        }}
      >
        <div className="min-w-0 flex-1">
          <Label htmlFor="admin-user-search">{t('users.searchLabel')}</Label>
          <Input
            id="admin-user-search"
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
          />
        </div>
        <Button type="submit" variant="primary">
          {t('users.searchAction')}
        </Button>
      </form>

      {users.isPending && (
        <p className="motion-safe:animate-pulse text-ink-soft" role="status">
          {t('users.loading')}
        </p>
      )}

      {users.isError && (
        <p role="alert">
          {t(
            isApiError(users.error) ? `errors.codes.${users.error.code}` : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      {users.data && (
        <p className="text-ink-soft" role="status">
          {t('users.total', {
            shown: number.format(items.length),
            total: number.format(total),
          })}
        </p>
      )}

      {users.data && items.length === 0 && <p className="text-ink">{t('users.empty')}</p>}

      {items.length > 0 && (
        <ul className="space-y-3">
          {items.map((user) => (
            <UserRow key={user.id} user={user} />
          ))}
        </ul>
      )}

      {users.data && (page > 1 || hasNextPage) && (
        <nav aria-label={t('users.pagination.page', { page })} className="flex gap-2">
          <Button
            disabled={page === 1}
            variant="neutral"
            onClick={() => setPage((current) => Math.max(1, current - 1))}
          >
            {t('users.pagination.previous')}
          </Button>
          <Button
            disabled={!hasNextPage}
            variant="neutral"
            onClick={() => setPage((current) => current + 1)}
          >
            {t('users.pagination.next')}
          </Button>
        </nav>
      )}
    </main>
  )
}
