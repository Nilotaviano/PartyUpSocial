import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { isApiError } from '../../api/client'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { FriendCodeInput } from './FriendCodeInput'
import { FriendList } from './FriendList'
import { FriendRequests } from './FriendRequests'
import { ShareCard } from './ShareCard'
import { friendKeys, getFriendRequests, getFriends } from './friendGraph'

function Loading() {
  const { t } = useTranslation('friends')
  return (
    <p aria-hidden="true" className="motion-safe:animate-pulse text-ink-soft">
      {t('status.loading')}
    </p>
  )
}

export function FriendsPage() {
  const navigate = useNavigate()
  const { t } = useTranslation(['friends', 'errors'])
  const [status, setStatus] = useState('')
  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })
  const requests = useQuery({
    queryKey: friendKeys.requests,
    queryFn: getFriendRequests,
  })

  const error = friends.error ?? requests.error
  const friendItems = friends.data ?? []
  const incoming = requests.data?.incoming ?? []
  const outgoing = requests.data?.outgoing ?? []

  return (
    <main data-testid="screen-friends">
      <PageScaffold>
        <PageHeader
          actions={
            <Link
              className={`inline-flex items-center gap-2 px-4 py-2 text-primary-ink bg-primary ${raisedInteractiveClasses}`}
              to="/friends/import/steam"
            >
              {t('actions.importSteam')}
            </Link>
          }
          title={t('pages.list')}
        />

        <p aria-live="polite" className="text-ink">
          {status}
        </p>
        {error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(error) ? error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </StatusBanner>
        )}

        <Section title={t('sections.findByCode')} titleId="find-by-code-heading">
          <FriendCodeInput
            onResolve={(friendCode) => navigate('/add/' + friendCode)}
          />
        </Section>

        <ShareCard />

        <Section
          title={`${t('sections.friends')} (${friendItems.length})`}
          titleId="friends-list-heading"
        >
          {friends.isPending ? (
            <Loading />
          ) : (
            <FriendList items={friendItems} onStatus={setStatus} />
          )}
        </Section>

        <Section
          title={`${t('sections.incoming')} (${incoming.length})`}
          titleId="incoming-heading"
        >
          {requests.isPending ? (
            <Loading />
          ) : (
            <FriendRequests
              direction="incoming"
              items={incoming}
              onStatus={setStatus}
            />
          )}
        </Section>

        <Section
          title={`${t('sections.outgoing')} (${outgoing.length})`}
          titleId="outgoing-heading"
        >
          {requests.isPending ? (
            <Loading />
          ) : (
            <FriendRequests
              direction="outgoing"
              items={outgoing}
              onStatus={setStatus}
            />
          )}
        </Section>
      </PageScaffold>
    </main>
  )
}
