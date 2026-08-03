import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
} from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import type { components } from '../../api/schema'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { validateFriendContinuation } from '../../auth/friendContinuation'

type ProfileView = components['schemas']['ProfileViewResponse']

const profileQueryKey = (username: string) => ['profile', username] as const

async function invalidateFriendGraph(
  queryClient: QueryClient,
  username?: string,
) {
  await Promise.all([
    username
      ? queryClient.invalidateQueries({ queryKey: profileQueryKey(username) })
      : Promise.resolve(),
    queryClient.invalidateQueries({ queryKey: ['friendCode'] }),
    queryClient.invalidateQueries({ queryKey: ['friends'] }),
    queryClient.invalidateQueries({ queryKey: ['friendRequests'] }),
    queryClient.invalidateQueries({ queryKey: ['blocks'] }),
  ])
}

async function getProfile(username: string) {
  const { data } = await client.GET('/api/v1/users/by-username/{username}', {
    params: { path: { username } },
  })
  if (!data) throw new Error('Profile response was empty.')
  return data.profile
}

function FriendError({ error }: { error: unknown }) {
  const { t } = useTranslation('errors')
  if (!error) return null
  return (
    <p role="alert">
      {t(isApiError(error) ? error.code : 'generic', {
        defaultValue: t('generic'),
      })}
    </p>
  )
}

type ProfileActionCardProps = {
  profile: ProfileView
  onChanged?: () => void
}

export function ProfileActionCard({
  profile,
  onChanged,
}: ProfileActionCardProps) {
  const { t } = useTranslation('friends')
  const queryClient = useQueryClient()
  const [status, setStatus] = useState('')

  const action = useMutation({
    mutationFn: async (
      kind: 'send' | 'accept' | 'reject' | 'cancel' | 'unfriend' | 'block',
    ) => {
      const friendshipId = profile.relationship.friendshipId
      if (kind === 'send') {
        await client.POST('/api/v1/friend-requests', {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: { targetUsername: profile.username },
        })
      } else if (kind === 'block') {
        await client.POST('/api/v1/blocks', {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: { targetUsername: profile.username },
        })
      } else if (!friendshipId) {
        throw new Error('Friendship identifier was missing.')
      } else if (kind === 'accept') {
        await client.POST('/api/v1/friend-requests/{friendshipId}/accept', {
          params: { header: { 'X-CSRF-TOKEN': '' }, path: { friendshipId } },
        })
      } else if (kind === 'reject') {
        await client.POST('/api/v1/friend-requests/{friendshipId}/reject', {
          params: { header: { 'X-CSRF-TOKEN': '' }, path: { friendshipId } },
        })
      } else if (kind === 'cancel') {
        await client.DELETE('/api/v1/friend-requests/{friendshipId}', {
          params: { header: { 'X-CSRF-TOKEN': '' }, path: { friendshipId } },
        })
      } else {
        await client.DELETE('/api/v1/friends/{friendshipId}', {
          params: { header: { 'X-CSRF-TOKEN': '' }, path: { friendshipId } },
        })
      }
      return kind
    },
    onSuccess: async (kind) => {
      const statusKeys = {
        send: 'status.requestSent',
        accept: 'status.requestAccepted',
        reject: 'status.requestRejected',
        cancel: 'status.requestCanceled',
        unfriend: 'status.unfriended',
        block: 'status.blocked',
      } as const
      setStatus(t(statusKeys[kind]))
      await invalidateFriendGraph(queryClient, profile.username)
      onChanged?.()
    },
  })

  const relationship = profile.relationship.kind
  const relationshipStatusKey = {
    none: 'status.none',
    incoming_pending: 'status.pendingIncoming',
    outgoing_pending: 'status.pendingOutgoing',
    accepted: 'status.friends',
  } as const
  return (
    <article
      className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm"
      data-disclosure={profile.disclosure}
    >
      {profile.disclosure === 'full' ? (
        <>
          <div className="flex items-center gap-3">
            <span
              aria-hidden="true"
              className="grid h-12 w-12 place-items-center rounded-full bg-indigo-100 text-xl"
            >
              {profile.avatar.emoji ?? '●'}
            </span>
            <div>
              <h2 className="text-lg font-semibold text-slate-950">
                {profile.displayName}
              </h2>
              <p className="font-mono text-sm text-slate-600">
                @{profile.username}
              </p>
            </div>
          </div>
          {profile.platforms.length > 0 && (
            <ul className="mt-4 space-y-1" aria-label={t('profile.platforms')}>
              {profile.platforms.map((platform) => (
                <li
                  key={`${platform.platform}:${platform.handle}`}
                  className="text-sm text-slate-700"
                >
                  {platform.platform}: {platform.handle}
                </li>
              ))}
            </ul>
          )}
        </>
      ) : (
        <>
          <h2 className="font-mono text-lg font-semibold text-slate-950">
            @{profile.username}
          </h2>
          <p className="sr-only">{t('profile.usernameOnly')}</p>
        </>
      )}

      <p className="mt-3 text-sm text-slate-600">
        {t(relationshipStatusKey[relationship])}
      </p>
      <div className="mt-4 flex flex-wrap gap-2">
        {relationship === 'none' && (
          <Button
            disabled={action.isPending}
            onClick={() => action.mutate('send')}
          >
            {t('actions.sendRequest')}
          </Button>
        )}
        {relationship === 'incoming_pending' && (
          <>
            <Button
              disabled={action.isPending}
              onClick={() => action.mutate('accept')}
            >
              {t('actions.accept')}
            </Button>
            <Button
              disabled={action.isPending}
              onClick={() => action.mutate('reject')}
            >
              {t('actions.reject')}
            </Button>
          </>
        )}
        {relationship === 'outgoing_pending' && (
          <Button
            disabled={action.isPending}
            onClick={() => action.mutate('cancel')}
          >
            {t('actions.cancelRequest')}
          </Button>
        )}
        <Button
          disabled={action.isPending}
          onClick={() => action.mutate('block')}
        >
          {t('actions.block')}
        </Button>
      </div>
      <p aria-live="polite" className="mt-3 text-sm text-emerald-700">
        {status}
      </p>
      <FriendError error={action.error} />
    </article>
  )
}

export function ProfileResolution({ username }: { username: string }) {
  const { t } = useTranslation('friends')
  const continuation = validateFriendContinuation('/u/' + username)
  const canonical = continuation?.slice(3)
  const profile = useQuery({
    queryKey: profileQueryKey(canonical ?? username),
    queryFn: () => getProfile(canonical!),
    enabled: canonical !== undefined,
  })

  return (
    <>
      {!canonical && <p role="alert">{t('profile.notFound')}</p>}
      {profile.isPending && canonical && <p>{t('status.loading')}</p>}
      <FriendError error={profile.error} />
      {profile.data && <ProfileActionCard profile={profile.data} />}
    </>
  )
}

export function ProfilePage() {
  const { t } = useTranslation('friends')
  const { username = '' } = useParams()
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [username])

  return (
    <main
      className="mx-auto max-w-2xl space-y-5 p-4 sm:p-8"
      data-testid="screen-friend-profile"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-2xl font-bold tracking-tight text-slate-950"
      >
        {t('pages.profile')}
      </h1>
      <ProfileResolution username={username} />
    </main>
  )
}
