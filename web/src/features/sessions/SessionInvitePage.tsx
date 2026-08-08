import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
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
  const { t } = useTranslation(['sessions', 'errors'])
  const { sessionId = '' } = useParams()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
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

  useEffect(() => {
    heading.current?.focus()
  }, [])

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
    <main
      className="mx-auto max-w-2xl space-y-6 p-4 sm:p-8"
      data-testid="screen-session-invite"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('invite.heading')}
      </h1>

      <p aria-live="polite" className="text-ink">
        {status}
      </p>

      {anyError && (
        <p role="alert">
          {t(
            isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      <section aria-labelledby="invite-registered-heading">
        <h2 id="invite-registered-heading" className="text-h2 font-display font-semibold text-ink">
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

      <section aria-labelledby="invite-guest-heading">
        <h2 id="invite-guest-heading" className="text-h2 font-display font-semibold text-ink">
          {t('invite.guest')}
        </h2>
        <label className="mt-2 block font-body font-semibold text-ink" htmlFor="guest-link-count">
          {t('create.guestLinks')}
        </label>
        <input
          className="mt-1 min-h-11 w-24 rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          id="guest-link-count"
          max={20}
          min={1}
          type="number"
          value={linkCount}
          onChange={(event) => setLinkCount(event.target.value)}
        />
        <Button
          aria-busy={createLinks.isPending}
          className="ml-2"
          disabled={createLinks.isPending}
          variant="neutral"
          onClick={() => createLinks.mutate()}
        >
          {t('invite.createLink')}
        </Button>

        {guestLinks.length > 0 && (
          <ul className="mt-3 space-y-2">
            {guestLinks.map((link) => (
              <li
                key={link.guestLinkId}
                className="flex items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-3"
              >
                <span className="min-w-0 flex-1 truncate font-mono text-ink">
                  {link.url}
                </span>
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
      </section>
    </main>
  )
}
