import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { FriendList } from './FriendList'
import { FriendRequests } from './FriendRequests'
import { ShareCard } from './ShareCard'
import { friendKeys, getFriendRequests, getFriends } from './friendGraph'

export function FriendsPage() {
  const { t } = useTranslation(['friends', 'errors'])
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })
  const requests = useQuery({
    queryKey: friendKeys.requests,
    queryFn: getFriendRequests,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const error = friends.error ?? requests.error
  const friendItems = friends.data ?? []
  const incoming = requests.data?.incoming ?? []
  const outgoing = requests.data?.outgoing ?? []

  return (
    <main
      className="mx-auto max-w-5xl space-y-8 p-4 sm:p-8"
      data-testid="screen-friends"
    >
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
        >
          {t('pages.list')}
        </h1>
        <Link
          className="inline-flex min-h-11 items-center rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          to="/friends/import/steam"
        >
          {t('actions.importSteam')}
        </Link>
      </header>

      <p aria-live="polite" className="text-sm text-emerald-700">
        {status}
      </p>
      {error && (
        <p role="alert">
          {t(isApiError(error) ? error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}

      <section aria-labelledby="friends-list-heading" className="space-y-4">
        <h2
          id="friends-list-heading"
          className="text-xl font-semibold text-slate-950"
        >
          {t('sections.friends')} ({friendItems.length})
        </h2>
        {friends.isPending ? (
          <p
            aria-hidden="true"
            className="motion-safe:animate-pulse text-slate-500"
          >
            {t('status.loading')}
          </p>
        ) : (
          <>
            {friendItems.length === 0 && <ShareCard />}
            <FriendList items={friendItems} onStatus={setStatus} />
          </>
        )}
      </section>

      <section aria-labelledby="incoming-heading" className="space-y-4">
        <h2
          id="incoming-heading"
          className="text-xl font-semibold text-slate-950"
        >
          {t('sections.incoming')} ({incoming.length})
        </h2>
        {requests.isPending ? (
          <p
            aria-hidden="true"
            className="motion-safe:animate-pulse text-slate-500"
          >
            {t('status.loading')}
          </p>
        ) : (
          <FriendRequests
            direction="incoming"
            items={incoming}
            onStatus={setStatus}
          />
        )}
      </section>

      <section aria-labelledby="outgoing-heading" className="space-y-4">
        <h2
          id="outgoing-heading"
          className="text-xl font-semibold text-slate-950"
        >
          {t('sections.outgoing')} ({outgoing.length})
        </h2>
        {requests.isPending ? (
          <p
            aria-hidden="true"
            className="motion-safe:animate-pulse text-slate-500"
          >
            {t('status.loading')}
          </p>
        ) : (
          <FriendRequests
            direction="outgoing"
            items={outgoing}
            onStatus={setStatus}
          />
        )}
      </section>
    </main>
  )
}
