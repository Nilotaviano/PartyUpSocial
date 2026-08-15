import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  controlRowClasses,
  fieldControlClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { friendKeys, getFriends } from '../friends/friendGraph'
import {
  createGuestLinks,
  getSessionDetail,
  inviteUsers,
  invalidateSessionQueries,
  revokeGuestLink,
  sessionKeys,
  type GuestLinkInvitation,
} from './sessionQueries'

type RevokeGuestLinkDialogProps = {
  pending: boolean
  onConfirm: () => void
}

function RevokeGuestLinkDialog({ pending, onConfirm }: RevokeGuestLinkDialogProps) {
  const { t } = useTranslation('sessions')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button disabled={pending} variant="danger">
          {t('invite.revoke')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="revoke-guest-link-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('invite.revoke')}
          </Dialog.Title>
          <Dialog.Description
            id="revoke-guest-link-description"
            className="mt-2 text-ink-soft"
          >
            {t('invite.revokeConfirm')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('invite.revoke')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export function SessionInvitePage() {
  const { t } = useTranslation(['sessions', 'errors', 'common'])
  const { sessionId = '' } = useParams()
  const queryClient = useQueryClient()
  const [status, setStatus] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [guestLinks, setGuestLinks] = useState<GuestLinkInvitation[]>([])
  const [linkCount, setLinkCount] = useState('1')

  const detail = useQuery({
    queryKey: sessionKeys.detail(sessionId),
    queryFn: () => getSessionDetail(sessionId),
    enabled: Boolean(sessionId),
    retry: false,
  })

  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })

  const invite = useMutation({
    mutationFn: () => inviteUsers(sessionId, selected),
    onSuccess: async () => {
      setStatus(t('invite.registered'))
      setSelected([])
      await invalidateSessionQueries(queryClient, sessionId)
    },
  })

  const createLinks = useMutation({
    mutationFn: () => createGuestLinks(sessionId, Number(linkCount)),
    onSuccess: (created) => {
      setGuestLinks((current) => [...current, ...created])
    },
  })

  const revoke = useMutation({
    mutationFn: (guestLinkId: string) => revokeGuestLink(sessionId, guestLinkId),
    onSuccess: (_result, guestLinkId) => {
      setGuestLinks((current) =>
        current.filter((link) => link.guestLinkId !== guestLinkId),
      )
    },
  })

  async function copy(url: string) {
    try {
      await navigator.clipboard.writeText(url)
      setStatus(t('invite.copySuccess'))
    } catch {
      setStatus(t('errors.generic'))
    }
  }

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/welcome" />
  }

  const anyError = invite.error ?? createLinks.error ?? revoke.error

  return (
    <main data-testid="screen-session-invite">
      <PageScaffold width="form">
        <PageHeader
          back={{ to: `/sessions/${sessionId}`, label: t('actions.back', { ns: 'common' }) }}
          title={t('invite.heading')}
        />

        <p aria-live="polite" className="text-ink">
          {status}
        </p>

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
          titleId="invite-registered-heading"
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

        <SectionCard title={t('invite.guest')} titleId="invite-guest-heading">
          <div className="flex flex-wrap items-end gap-2">
            <div className="flex flex-col gap-1.5">
              <label
                className="font-body font-semibold text-ink"
                htmlFor="guest-link-count"
              >
                {t('create.guestLinks')}
              </label>
              <input
                className={`${fieldControlClasses} w-24`}
                id="guest-link-count"
                max={20}
                min={1}
                type="number"
                value={linkCount}
                onChange={(event) => setLinkCount(event.target.value)}
              />
            </div>
            <Button
              aria-busy={createLinks.isPending}
              disabled={createLinks.isPending}
              variant="neutral"
              onClick={() => createLinks.mutate()}
            >
              {t('invite.createLink')}
            </Button>
          </div>

          {guestLinks.length > 0 && (
            <ul>
              {guestLinks.map((link) => (
                <li
                  key={link.guestLinkId}
                  className="grid grid-cols-[1fr_auto] items-center gap-2 border-t border-ink/25 py-2 first:border-t-0"
                >
                  <span className="min-w-0 truncate font-mono text-ink">{link.url}</span>
                  <span className="flex flex-none gap-2">
                    <Button
                      aria-label={t('a11y.copyLink')}
                      type="button"
                      variant="ghost"
                      onClick={() => void copy(link.url)}
                    >
                      {t('invite.copyLink')}
                    </Button>
                    <RevokeGuestLinkDialog
                      pending={revoke.isPending}
                      onConfirm={() => revoke.mutate(link.guestLinkId)}
                    />
                  </span>
                </li>
              ))}
            </ul>
          )}
        </SectionCard>
      </PageScaffold>
    </main>
  )
}
