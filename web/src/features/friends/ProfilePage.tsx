import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import type { components } from '../../api/schema'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { validateFriendContinuation } from '../../auth/friendContinuation'
import { UnfriendDialog } from './FriendList'
import {
  friendKeys,
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendGraphAction,
} from './friendGraph'

type ProfileView = components['schemas']['ProfileViewResponse']

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
    <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
      {t(isApiError(error) ? error.code : 'generic', {
        defaultValue: t('generic'),
      })}
    </p>
  )
}

type ProfileActionCardProps = {
  profile: ProfileView
  friendCode?: string
  onChanged?: (kind: FriendGraphAction['kind']) => void
}

export function ProfileActionCard({
  profile,
  friendCode,
  onChanged,
}: ProfileActionCardProps) {
  const { t } = useTranslation('friends')
  const queryClient = useQueryClient()
  const [status, setStatus] = useState('')

  function input(kind: FriendGraphAction['kind']): FriendGraphAction {
    const base = { username: profile.username, friendCode }
    if (kind === 'send') return { kind, ...base }
    if (kind === 'block') return { kind, ...base }
    if (kind === 'unblock') {
      throw new Error('Unblock is not a profile action.')
    }
    const friendshipId = profile.relationship.friendshipId
    if (!friendshipId) throw new Error('Friendship identifier was missing.')
    return { kind, friendshipId, ...base }
  }

  const action = useMutation({
    mutationFn: async (kind: FriendGraphAction['kind']) => {
      const graphAction = input(kind)
      const result = await runFriendGraphAction(graphAction)
      return { graphAction, result }
    },
    onSuccess: async ({ graphAction }) => {
      const statusKeys = {
        send: 'status.requestSent',
        accept: 'status.requestAccepted',
        reject: 'status.requestRejected',
        cancel: 'status.requestCanceled',
        unfriend: 'status.unfriended',
        block: 'status.blocked',
        unblock: 'status.unblocked',
      } as const
      setStatus(t(statusKeys[graphAction.kind]))
      await invalidateFriendGraph(queryClient, graphAction)
      onChanged?.(graphAction.kind)
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
                  className="break-all text-sm text-slate-700"
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
            aria-busy={action.isPending}
            className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
            disabled={action.isPending}
            onClick={() => action.mutate('send')}
          >
            {t('actions.sendRequest')}
          </Button>
        )}
        {relationship === 'incoming_pending' && (
          <>
            <Button
              aria-busy={action.isPending}
              className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
              disabled={action.isPending}
              onClick={() => action.mutate('accept')}
            >
              {t('actions.accept')}
            </Button>
            <Button
              aria-busy={action.isPending}
              className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
              disabled={action.isPending}
              onClick={() => action.mutate('reject')}
            >
              {t('actions.reject')}
            </Button>
          </>
        )}
        {relationship === 'outgoing_pending' && (
          <Button
            aria-busy={action.isPending}
            className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
            disabled={action.isPending}
            onClick={() => action.mutate('cancel')}
          >
            {t('actions.cancelRequest')}
          </Button>
        )}
        {relationship === 'accepted' && profile.relationship.friendshipId && (
          <UnfriendDialog
            pending={action.isPending}
            username={profile.username}
            onConfirm={async () => {
              await action.mutateAsync('unfriend')
            }}
          />
        )}
        <Button
          aria-busy={action.isPending}
          className="min-h-11 rounded-md border border-red-300 px-4 py-2 text-red-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700 disabled:opacity-60"
          disabled={action.isPending}
          onClick={() => action.mutate('block')}
        >
          {t('actions.block')}
        </Button>
      </div>
      <p className="mt-3 text-sm text-slate-600">
        {t('profile.blockConsequence')}
      </p>
      <p aria-live="polite" className="mt-3 text-sm text-emerald-700">
        {status}
      </p>
      <FriendError error={action.error} />
    </article>
  )
}

export function ProfileResolution({ username }: { username: string }) {
  const { t } = useTranslation('friends')
  const [hiddenUsername, setHiddenUsername] = useState<string>()
  const continuation = validateFriendContinuation('/u/' + username)
  const canonical = continuation?.slice(3)
  const hidden = canonical !== undefined && hiddenUsername === canonical
  const profile = useQuery({
    queryKey: friendKeys.profile(canonical ?? username),
    queryFn: () => getProfile(canonical!),
    enabled: canonical !== undefined,
  })

  return (
    <>
      {!canonical && <p role="alert">{t('profile.notFound')}</p>}
      {profile.isPending && canonical && <p>{t('status.loading')}</p>}
      <FriendError error={profile.error} />
      <p aria-live="polite">{hidden ? t('status.blocked') : ''}</p>
      {hidden && <p>{t('profile.notFound')}</p>}
      {profile.data && !hidden && (
        <ProfileActionCard
          profile={profile.data}
          onChanged={(kind) => {
            if (kind === 'block') setHiddenUsername(canonical)
          }}
        />
      )}
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
