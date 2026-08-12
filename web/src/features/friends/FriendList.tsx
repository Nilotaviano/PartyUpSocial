import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { AvatarDisc } from './AvatarDisc'
import { avatarImgUrl } from './avatarImage'
import {
  friendKeys,
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendItem,
} from './friendGraph'

type UnfriendDialogProps = {
  username: string
  pending: boolean
  onConfirm: () => Promise<void>
}

export function UnfriendDialog({
  username,
  pending,
  onConfirm,
}: UnfriendDialogProps) {
  const { t } = useTranslation(['friends', 'settings'])
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  async function confirm() {
    try {
      await onConfirm()
      setOpen(false)
    } catch {
      return
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-label={`${t('actions.unfriend')} @${username}`}
          disabled={pending}
          variant="danger"
        >
          {t('actions.unfriend')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="unfriend-dialog-description"
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="font-display text-h2 font-semibold text-ink">
            {t('dialogs.unfriendTitle', { username: `@${username}` })}
          </Dialog.Title>
          <Dialog.Description
            id="unfriend-dialog-description"
            className="mt-2 text-ink-soft"
          >
            {t('dialogs.unfriendBody')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('actions.cancel', { ns: 'settings' })}
              </button>
            </Dialog.Close>
            <Button
              aria-busy={pending}
              disabled={pending}
              variant="danger"
              onClick={() => void confirm()}
            >
              {t('actions.confirmUnfriend')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type FriendListProps = {
  items: FriendItem[]
  onStatus: (message: string) => void
}

export function FriendList({ items, onStatus }: FriendListProps) {
  const { t } = useTranslation(['friends', 'errors'])
  const queryClient = useQueryClient()
  const action = useMutation({
    mutationFn: runFriendGraphAction,
    onSuccess: async (_result, input) => {
      if (input.kind === 'block') {
        queryClient.setQueryData<FriendItem[]>(friendKeys.friends, (current) =>
          current?.filter((friend) => friend.username !== input.username),
        )
      }
      onStatus(
        t(input.kind === 'block' ? 'status.blocked' : 'status.unfriended'),
      )
      await invalidateFriendGraph(queryClient, input)
    },
  })

  if (items.length === 0) return <p className="text-ink-soft">{t('empty.friends')}</p>

  return (
    <ul className="grid gap-4 sm:grid-cols-2">
      {items.map((friend) => {
        const base = {
          username: friend.username,
          friendshipId: friend.friendshipId,
        }
        return (
          <li key={friend.friendshipId}>
            <article className="flex h-full flex-col justify-between gap-4 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
              <Link
                className="flex min-h-11 items-center gap-3 rounded-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                to={`/u/${friend.username}`}
              >
                {/*
                  AVATARS_DESIGN.md §3.5: every AvatarDisc call site seeds on userId, so the
                  same person's fill color is consistent across every surface.
                */}
                <AvatarDisc
                  emoji={friend.avatar.emoji}
                  imgUrl={avatarImgUrl(friend.avatar)}
                  seed={friend.userId}
                />
                <span className="min-w-0">
                  <span className="block truncate font-display font-semibold text-ink">
                    {friend.displayName}
                  </span>
                  <span className="block truncate font-mono text-meta text-ink-soft">
                    @{friend.username}
                  </span>
                </span>
              </Link>
              <div className="flex flex-wrap gap-2">
                <UnfriendDialog
                  pending={action.isPending}
                  username={friend.username}
                  onConfirm={async () => {
                    await action.mutateAsync({ kind: 'unfriend', ...base })
                  }}
                />
                <Button
                  aria-busy={action.isPending}
                  aria-label={`${t('actions.block')} @${friend.username}`}
                  disabled={action.isPending}
                  variant="danger"
                  onClick={() => action.mutate({ kind: 'block', ...base })}
                >
                  {t('actions.block')}
                </Button>
              </div>
              <p className="text-meta text-ink-soft">
                {t('profile.blockConsequence')}
              </p>
            </article>
          </li>
        )
      })}
      {action.error && (
        <li className="sm:col-span-2">
          <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
            {t(isApiError(action.error) ? action.error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </p>
        </li>
      )}
    </ul>
  )
}
