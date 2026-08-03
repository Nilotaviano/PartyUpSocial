import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { client, isApiError } from '../../api/client'
import {
  isWellFormedFriendCode,
  normalizeFriendCode,
} from '../../auth/friendContinuation'
import { FriendCodeInput } from './FriendCodeInput'
import { ProfileActionCard } from './ProfilePage'
import { friendKeys } from './friendGraph'

async function resolveFriendCode(friendCode: string) {
  const { data } = await client.GET('/api/v1/friend-codes/{friendCode}', {
    params: { path: { friendCode } },
  })
  if (!data) throw new Error('Friend-code response was empty.')
  return data.profile
}

export function FriendCodeResolution({ friendCode }: { friendCode: string }) {
  const { t } = useTranslation(['friends', 'errors'])
  const [hiddenCode, setHiddenCode] = useState<string>()
  const normalized = normalizeFriendCode(friendCode)
  const hidden = hiddenCode === normalized
  const valid = isWellFormedFriendCode(normalized)
  const resolution = useQuery({
    queryKey: friendKeys.friendCode(normalized),
    queryFn: () => resolveFriendCode(normalized),
    enabled: valid,
  })

  return (
    <>
      {!valid && friendCode && (
        <p role="alert">{t('friend_code_invalid', { ns: 'errors' })}</p>
      )}
      {resolution.isPending && valid && <p>{t('status.loading')}</p>}
      {resolution.error && (
        <p role="alert">
          {t(isApiError(resolution.error) ? resolution.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
      <p aria-live="polite">{hidden ? t('status.blocked') : ''}</p>
      {hidden && <p>{t('profile.notFound')}</p>}
      {resolution.data && !hidden && (
        <ProfileActionCard
          friendCode={normalized}
          profile={resolution.data}
          onChanged={(kind) => {
            if (kind === 'block') setHiddenCode(normalized)
          }}
        />
      )}
    </>
  )
}

export function AddFriendPage() {
  const { t } = useTranslation('friends')
  const navigate = useNavigate()
  const { code = '' } = useParams()
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [code])

  return (
    <main
      className="mx-auto max-w-2xl space-y-5 p-4 sm:p-8"
      data-testid="screen-add-friend"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-2xl font-bold tracking-tight text-slate-950"
      >
        {t('pages.add')}
      </h1>
      <FriendCodeInput
        key={code}
        initialValue={normalizeFriendCode(code)}
        onResolve={(friendCode) => navigate('/add/' + friendCode)}
      />
      <FriendCodeResolution friendCode={code} />
    </main>
  )
}
