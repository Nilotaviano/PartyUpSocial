import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { friendKeys, getFriends } from '../friends/friendGraph'
import { getPollDetail, invitePollUsers, pollKeys } from './pollQueries'

/**
 * D-193: the poll analogue of `SessionInvitePage` — organizer-only, registered-friends-only
 * (mid-flight polls have no guest-link half here; that lives on `PollDetailPage`'s own guest
 * panel). Mirrors the session page's friend checklist exactly (semantics, not markup padding).
 */
export function PollInvitePage() {
  const { t } = useTranslation(['polls', 'errors'])
  const { pollId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const [selected, setSelected] = useState<string[]>([])

  const detail = useQuery({
    queryKey: pollKeys.detail(pollId),
    queryFn: () => getPollDetail(pollId),
    enabled: Boolean(pollId),
    retry: false,
  })

  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const invite = useMutation({
    mutationFn: () => invitePollUsers(pollId, selected),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: pollKeys.detail(pollId) })
      navigate(`/polls/${pollId}`)
    },
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/polls" />
  }

  const anyError = invite.error

  return (
    <main
      className="mx-auto max-w-2xl space-y-6 p-4 sm:p-8"
      data-testid="screen-poll-invite"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('invite.heading')}
      </h1>

      {anyError && (
        <p role="alert">
          {t(
            isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      <section aria-labelledby="poll-invite-registered-heading">
        <h2 id="poll-invite-registered-heading" className="text-h2 font-display font-semibold text-ink">
          {t('invite.registered')}
        </h2>
        <ul className="mt-2 space-y-1">
          {(friends.data ?? []).map((friend) => (
            <li key={friend.friendshipId}>
              <label className="inline-flex min-h-11 items-center gap-2 text-ink">
                <input
                  checked={selected.includes(friend.userId)}
                  type="checkbox"
                  onChange={(event) => {
                    const checked = event.target.checked
                    setSelected((current) =>
                      checked
                        ? [...current, friend.userId]
                        : current.filter((id) => id !== friend.userId),
                    )
                  }}
                />
                <AvatarDisc
                  emoji={friend.avatar.emoji}
                  imgUrl={avatarImgUrl(friend.avatar)}
                  seed={friend.userId}
                  size="xs"
                />
                {friend.displayName} (@{friend.username})
              </label>
            </li>
          ))}
        </ul>
        <Button
          aria-busy={invite.isPending}
          className="mt-3"
          disabled={invite.isPending || selected.length === 0}
          variant="primary"
          onClick={() => invite.mutate()}
        >
          {t('invite.registered')}
        </Button>
      </section>
    </main>
  )
}
