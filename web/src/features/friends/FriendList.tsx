import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dices } from 'lucide-react'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { ActionMenu, ActionMenuItem } from '../../components/ui/actionMenu'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { EmptyState } from '../../components/shell/SectionCard'
import { IdentityRow } from '../../components/ui/identityRow'
import {
  useActionMenu,
  useActionMenuDialog,
} from '../../components/ui/useActionMenu'
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
  /**
   * Controlled composition (STYLING_OVERHAUL_DESIGN.md §3.2): when the dialog is
   * raised from an ActionMenu item the menu owns the open state and renders no
   * trigger of its own here, and focus returns to that menu's trigger on close.
   */
  dialogProps?: { open: boolean; onOpenChange: (open: boolean) => void }
  contentProps?: { onCloseAutoFocus: (event: Event) => void }
}

export function UnfriendDialog({
  username,
  pending,
  onConfirm,
  dialogProps,
  contentProps,
}: UnfriendDialogProps) {
  const { t } = useTranslation(['friends', 'settings'])
  const [selfOpen, setSelfOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const open = dialogProps ? dialogProps.open : selfOpen
  const setOpen = dialogProps ? dialogProps.onOpenChange : setSelfOpen

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
      {!dialogProps && (
        <Dialog.Trigger asChild>
          <Button
            aria-label={`${t('actions.unfriend')} @${username}`}
            className="text-danger"
            disabled={pending}
            variant="ghost"
          >
            {t('actions.unfriend')}
          </Button>
        </Dialog.Trigger>
      )}
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          {...contentProps}
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

type FriendRowProps = {
  friend: FriendItem
  pending: boolean
  onUnfriend: () => Promise<void>
  onBlock: () => void
}

/**
 * One hairline person row (§3.3): the shipped identity link keeps its
 * display-name-then-username accessible name, and both destructive actions move
 * behind the row's ActionMenu so nothing rests red in a list of friends.
 */
function FriendRow({ friend, pending, onUnfriend, onBlock }: FriendRowProps) {
  const { t } = useTranslation('friends')
  const menu = useActionMenu()
  const unfriend = useActionMenuDialog(menu)

  return (
    <li className="border-t border-ink/25">
      <div className="flex items-center gap-2 py-2">
        <Link
          className="flex min-h-11 min-w-0 flex-1 items-center rounded-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          to={`/u/${friend.username}`}
        >
          {/*
            AVATARS_DESIGN.md §3.5: every AvatarDisc call site seeds on userId, so the
            same person's fill color is consistent across every surface.
          */}
          <IdentityRow
            avatar={
              <AvatarDisc
                emoji={friend.avatar.emoji}
                imgUrl={avatarImgUrl(friend.avatar)}
                seed={friend.userId}
              />
            }
            className="flex-1"
            displayName={friend.displayName}
            username={friend.username}
          />
        </Link>
        <ActionMenu
          {...menu.menuProps}
          label={`${t('actions.more', { ns: 'common' })} @${friend.username}`}
        >
          <ActionMenuItem destructive disabled={pending} {...unfriend.itemProps}>
            {t('actions.unfriend')}
          </ActionMenuItem>
          <ActionMenuItem
            busy={pending}
            description={t('profile.blockConsequence')}
            destructive
            disabled={pending}
            onSelect={onBlock}
          >
            {`${t('actions.block')} @${friend.username}`}
          </ActionMenuItem>
        </ActionMenu>
      </div>
      <UnfriendDialog
        contentProps={unfriend.contentProps}
        dialogProps={unfriend.dialogProps}
        pending={pending}
        username={friend.username}
        onConfirm={onUnfriend}
      />
    </li>
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

  if (items.length === 0) {
    return (
      <EmptyState
        action={
          <a
            className={`inline-flex items-center gap-2 bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
            href="#friend-share-heading"
          >
            {t('sections.share')}
          </a>
        }
        icon={Dices}
        title={t('empty.friends')}
      />
    )
  }

  return (
    <>
      <ul className="flex flex-col border-b border-ink/25">
        {items.map((friend) => {
          const base = {
            username: friend.username,
            friendshipId: friend.friendshipId,
          }
          return (
            <FriendRow
              key={friend.friendshipId}
              friend={friend}
              pending={action.isPending}
              onBlock={() => action.mutate({ kind: 'block', ...base })}
              onUnfriend={async () => {
                await action.mutateAsync({ kind: 'unfriend', ...base })
              }}
            />
          )
        })}
      </ul>
      {action.error && (
        <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
          {t(isApiError(action.error) ? action.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
    </>
  )
}
