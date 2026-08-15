import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { controlRowClasses } from '../../components/ui/classes'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
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
  const { t } = useTranslation(['polls', 'errors', 'common'])
  const { pollId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
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
    <main data-testid="screen-poll-invite">
      <PageScaffold width="form">
        <PageHeader
          back={{ to: `/polls/${pollId}`, label: t('actions.back', { ns: 'common' }) }}
          title={t('invite.heading')}
        />

        {anyError && (
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        <Section
          actions={
            <Button
              aria-busy={invite.isPending}
              disabled={invite.isPending || selected.length === 0}
              variant="primary"
              onClick={() => invite.mutate()}
            >
              {t('invite.registered')}
            </Button>
          }
          title={t('invite.registered')}
          titleId="poll-invite-registered-heading"
        >
          <ul className="flex flex-col gap-2">
            {(friends.data ?? []).map((friend) => (
              <li key={friend.friendshipId}>
                <label className={`${controlRowClasses} text-ink`}>
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
        </Section>
      </PageScaffold>
    </main>
  )
}
