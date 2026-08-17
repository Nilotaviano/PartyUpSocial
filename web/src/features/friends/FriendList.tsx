import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { Dices } from 'lucide-react'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { ActionMenu, ActionMenuItem } from '../../components/ui/actionMenu'
import { Button } from '../../components/ui/button'
import { neutralCtaClasses, raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { EmptyState } from '../../components/shell/SectionCard'
import { IdentityRow } from '../../components/ui/identityRow'
import {
  useActionMenu,
  useActionMenuDialog,
} from '../../components/ui/useActionMenu'
import { useToast } from '../../components/ui/toastStore'
import { getGroupDetail, getGroupList, groupKeys, removeGroupMember } from '../groups/groupQueries'
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
  userId: string
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

/**
 * D-210: groups the ACTOR owns that the unfriend target belongs to
 * (Accepted membership only — an outstanding invite isn't "remaining in"
 * anything yet). The list read (`groupKeys.list`) carries no per-group
 * roster, so this resolves it with one detail fetch per owned group,
 * fired only while the dialog is open — the sanctioned per-group-detail
 * lookup (no single-call roster-membership contract exists on the wire).
 *
 * The fan-out is bounded by D-208's 50-owned-group cap, so a single dialog
 * open can issue up to 50 detail requests; `staleTime: 60_000` is the trade
 * that keeps repeat opens (e.g. reopening the dialog on the next row) from
 * re-firing that whole fan-out within the same minute, at the cost of a
 * membership change elsewhere not showing up here for up to 60s.
 */
function useOwnedGroupsContaining(userId: string, enabled: boolean) {
  const list = useQuery({ queryKey: groupKeys.list, queryFn: getGroupList, enabled })
  const ownedIds = list.data?.owned.map((group) => group.id) ?? []

  const details = useQueries({
    queries: ownedIds.map((groupId) => ({
      queryKey: groupKeys.detail(groupId),
      queryFn: () => getGroupDetail(groupId),
      enabled,
      staleTime: 60_000,
    })),
  })

  return details
    .filter((query) =>
      query.data?.members?.some((member) => member.userId === userId && member.status === 'accepted'),
    )
    .map((query) => ({ id: query.data!.id, name: query.data!.name }))
}

export function UnfriendDialog({
  username,
  userId,
  pending,
  onConfirm,
  dialogProps,
  contentProps,
}: UnfriendDialogProps) {
  const { t } = useTranslation(['friends', 'settings', 'groups'])
  const showToast = useToast()
  const [selfOpen, setSelfOpen] = useState(false)
  const [alsoRemove, setAlsoRemove] = useState(true)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const open = dialogProps ? dialogProps.open : selfOpen
  const setOpen = dialogProps ? dialogProps.onOpenChange : setSelfOpen
  const sharedGroups = useOwnedGroupsContaining(userId, open)

  async function confirm() {
    try {
      await onConfirm()
    } catch {
      // The unfriend itself failed — leave the dialog open; FriendList's own
      // mutation-error region below the list surfaces this.
      return
    }

    // The unfriend is done regardless of what happens next, so the dialog
    // closes here rather than after the best-effort group-removal step.
    setOpen(false)

    // D-210: member-removal mutations fire only after the unfriend mutation
    // has succeeded, and only for the groups the actor actually saw listed.
    if (alsoRemove && sharedGroups.length > 0) {
      try {
        await Promise.all(sharedGroups.map((group) => removeGroupMember(group.id, userId)))
      } catch {
        showToast({ message: t('errors.generic', { ns: 'groups' }) })
      }
    }
  }

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (next) setAlsoRemove(true)
        setOpen(next)
      }}
    >
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
          {sharedGroups.length > 0 && (
            <div className="mt-3 rounded-xl border border-border bg-muted/40 p-3">
              <p className="text-[12.5px] text-ink-soft">
                {t('unfriend.remainsIn', {
                  names: sharedGroups.map((group) => group.name).join(', '),
                  ns: 'groups',
                })}
              </p>
              <label className="mt-2 flex min-h-11 cursor-pointer items-center gap-2 text-[13px] text-ink">
                <input
                  checked={alsoRemove}
                  type="checkbox"
                  onChange={(event) => setAlsoRemove(event.target.checked)}
                />
                {t('unfriend.alsoRemove', { ns: 'groups' })}
              </label>
            </div>
          )}
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
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
 * One crew row, in the prototype's item grammar (grid, bordered surface —
 * `friends.tsx`'s `The crew` list): the shipped identity link keeps its
 * display-name-then-username accessible name, and both destructive actions
 * stay behind the row's ActionMenu so nothing rests red in a list of
 * friends. `AvatarDisc`'s `xs` size (36px) is the closest fit to the
 * prototype's `size-10` (40px) disc.
 */
function FriendRow({ friend, pending, onUnfriend, onBlock }: FriendRowProps) {
  const { t } = useTranslation('friends')
  const menu = useActionMenu()
  const unfriend = useActionMenuDialog(menu)

  return (
    <li className="grid grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-3 rounded-xl border border-border bg-background px-3 py-2.5">
      {/*
        AVATARS_DESIGN.md §3.5: every AvatarDisc call site seeds on userId, so the
        same person's fill color is consistent across every surface.
      */}
      <AvatarDisc
        emoji={friend.avatar.emoji}
        imgUrl={avatarImgUrl(friend.avatar)}
        seed={friend.userId}
        size="xs"
      />
      <Link
        className="flex min-h-11 min-w-0 items-center rounded-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        to={`/u/${friend.username}`}
      >
        <IdentityRow displayName={friend.displayName} username={friend.username} />
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
      <UnfriendDialog
        contentProps={unfriend.contentProps}
        dialogProps={unfriend.dialogProps}
        pending={pending}
        userId={friend.userId}
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
      <ul className="space-y-2">
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
