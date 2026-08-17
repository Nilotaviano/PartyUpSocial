import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Crown, DoorOpen, MailPlus, Pencil, Trash2, UsersRound } from 'lucide-react'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { useSession } from '../../auth/useSession'
import { EmptyState, SectionCard } from '../../components/shell/SectionCard'
import { PageHeader } from '../../components/shell/PageHeader'
import { Button } from '../../components/ui/button'
import { fieldControlClasses, neutralCtaClasses, primaryCtaClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { IdentityRow } from '../../components/ui/identityRow'
import { useToast } from '../../components/ui/toastStore'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { friendKeys, getFriends, type FriendItem } from '../friends/friendGraph'
import { FriendMultiSelect, Tag } from './GroupBits'
import {
  deleteGroup,
  getGroupDetail,
  groupKeys,
  inviteGroupMembers,
  removeGroupMember,
  renameGroup,
  type FriendGroupDetail,
  type FriendGroupMember,
} from './groupQueries'

/**
 * Ported from the Lovable reference (`src/routes/groups.$groupId.tsx`).
 * Adaptations mirror `GroupBits.tsx`'s file header: `AvatarDisc`/`IdentityRow`
 * replace the prototype's own `Avatar`, roster identity renders straight off
 * the server response (`FriendGroupMemberResponse.displayName`/`avatar`/
 * `masked`, plus the detail's `ownerDisplayName`/`ownerAvatar`/`ownerMasked`)
 * rather than a client-side friends-list join — a retained non-friend member
 * (D-200) still gets a real name, and a D-076-masked blocked-pair member
 * renders anonymous, both server-decided. Every dialog follows the
 * `SessionDiscardDialog` contract instead of the prototype's shadcn
 * `Dialog`/`AlertDialog`. The prototype's single `AlertDialog` covering
 * delete/leave/remove is one local `ConfirmDialog` here, parameterised the
 * same way.
 */

type ConfirmKind = { kind: 'delete' } | { kind: 'leave' } | { kind: 'remove'; userId: string }

type RosterPerson = {
  userId: string
  displayName: string
  avatar?: { emoji?: string | null; imgUrl?: string | null } | null
  masked: boolean
  isOwner: boolean
}

function ownerRosterPerson(group: FriendGroupDetail, t: (key: string) => string): RosterPerson {
  return {
    userId: group.ownerId,
    displayName: group.ownerMasked ? t('member.anonymous') : (group.ownerDisplayName ?? t('member.unknown')),
    avatar: group.ownerAvatar,
    masked: group.ownerMasked,
    isOwner: true,
  }
}

function memberRosterPerson(member: FriendGroupMember, t: (key: string) => string): RosterPerson {
  return {
    userId: member.userId,
    displayName: member.masked ? t('member.anonymous') : (member.displayName ?? t('member.unknown')),
    avatar: member.avatar,
    masked: member.masked,
    isOwner: false,
  }
}

function RenameDialog({
  open,
  onOpenChange,
  currentName,
  onSave,
  triggerRef,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  currentName: string
  onSave: (name: string) => void
  triggerRef: React.RefObject<HTMLElement | null>
}) {
  const { t } = useTranslation('groups')
  const nameField = useRef<HTMLInputElement>(null)
  const [name, setName] = useState(currentName)

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (next) setName(currentName)
        onOpenChange(next)
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="rename-group-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            nameField.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('rename.title')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="rename-group-dialog-description">
            {t('rename.description')}
          </Dialog.Description>
          <div className="mt-4 space-y-1.5">
            <label className="font-display text-[12px] font-bold text-ink" htmlFor="rename-group-name">
              {t('newGroup.nameLabel')}
            </label>
            <input
              className={fieldControlClasses}
              id="rename-group-name"
              maxLength={50}
              ref={nameField}
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
            <p className="text-[12px] text-ink-soft">{t('newGroup.nameCount', { count: name.length })}</p>
          </div>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button className={neutralCtaClasses} type="button" onClick={() => onOpenChange(false)}>
              {t('actions.cancel')}
            </button>
            <Button
              disabled={name.trim().length === 0}
              type="button"
              variant="primary"
              onClick={() => {
                onSave(name.trim())
                onOpenChange(false)
              }}
            >
              {t('rename.submit')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

function InviteDialog({
  open,
  onOpenChange,
  candidates,
  onInvite,
  triggerRef,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  candidates: FriendItem[]
  onInvite: (userIds: string[]) => void
  triggerRef: React.RefObject<HTMLElement | null>
}) {
  const { t } = useTranslation('groups')
  const [picked, setPicked] = useState<string[]>([])

  function close() {
    onOpenChange(false)
    setPicked([])
  }

  return (
    <Dialog.Root open={open} onOpenChange={(next) => (next ? onOpenChange(true) : close())}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="invite-group-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('invite.dialogTitle')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="invite-group-dialog-description">
            {t('invite.dialogDescription')}
          </Dialog.Description>
          <div className="mt-4">
            <FriendMultiSelect
              candidates={candidates}
              legend={t('invite.legend')}
              selected={picked}
              onToggle={(userId) =>
                setPicked((current) =>
                  current.includes(userId) ? current.filter((id) => id !== userId) : [...current, userId],
                )
              }
            />
          </div>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button className={neutralCtaClasses} type="button" onClick={close}>
              {t('actions.cancel')}
            </button>
            <Button
              disabled={picked.length === 0}
              type="button"
              variant="primary"
              onClick={() => {
                onInvite(picked)
                close()
              }}
            >
              {t('invite.submit')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/**
 * Unlike the other dialogs, this one has no single fixed trigger element (it
 * opens from the header's "Leave" action, the danger-zone "Delete" action, or
 * any roster row's "Remove" button) — so it skips the explicit
 * `onCloseAutoFocus` redirect the rest of this file uses and relies on
 * Radix's own default: focus returns to whichever element was active before
 * the dialog opened, same as `RevokeGuestLinkDialog` (`SessionInvitePage.tsx`).
 */
function ConfirmDialog({
  confirm,
  onOpenChange,
  onConfirm,
  groupName,
  memberName,
}: {
  confirm: ConfirmKind | null
  onOpenChange: (open: boolean) => void
  onConfirm: () => void
  groupName: string
  memberName: string
}) {
  const { t } = useTranslation('groups')
  const cancelButton = useRef<HTMLButtonElement>(null)

  const title =
    confirm?.kind === 'delete'
      ? t('confirm.delete.title', { group: groupName })
      : confirm?.kind === 'leave'
        ? t('confirm.leave.title', { group: groupName })
        : confirm?.kind === 'remove'
          ? t('confirm.remove.title', { name: memberName })
          : ''
  const description =
    confirm?.kind === 'delete'
      ? t('confirm.delete.description')
      : confirm?.kind === 'leave'
        ? t('confirm.leave.description')
        : confirm?.kind === 'remove'
          ? t('confirm.remove.description')
          : ''
  const actionLabel =
    confirm?.kind === 'delete'
      ? t('confirm.delete.action')
      : confirm?.kind === 'leave'
        ? t('confirm.leave.action')
        : t('confirm.remove.action')

  return (
    <Dialog.Root open={confirm !== null} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="confirm-group-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">{title}</Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="confirm-group-dialog-description">
            {description}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button
              className={neutralCtaClasses}
              ref={cancelButton}
              type="button"
              onClick={() => onOpenChange(false)}
            >
              {t('actions.cancel')}
            </button>
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                onConfirm()
                onOpenChange(false)
              }}
            >
              {actionLabel}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export function GroupDetailPage() {
  const { t } = useTranslation(['groups', 'common'])
  const { groupId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const showToast = useToast()
  const { data: session } = useSession()
  const friends = useQuery({ queryKey: friendKeys.friends, queryFn: getFriends })

  const [renaming, setRenaming] = useState(false)
  const [inviting, setInviting] = useState(false)
  const [confirm, setConfirm] = useState<ConfirmKind | null>(null)
  const confirmMemberId = confirm?.kind === 'remove' ? confirm.userId : ''

  const renameButton = useRef<HTMLButtonElement>(null)
  const inviteButton = useRef<HTMLButtonElement>(null)
  const leaveButton = useRef<HTMLButtonElement>(null)

  const detail = useQuery({
    queryKey: groupKeys.detail(groupId),
    queryFn: () => getGroupDetail(groupId),
    enabled: Boolean(groupId),
    retry: false,
  })

  function reportError(error: unknown) {
    showToast({
      message: isApiError(error)
        ? t(`errors.codes.${error.code}`, { defaultValue: t('errors.generic') })
        : t('errors.generic'),
    })
  }

  async function invalidateGroup() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: groupKeys.detail(groupId) }),
      queryClient.invalidateQueries({ queryKey: groupKeys.list }),
    ])
  }

  const rename = useMutation({
    mutationFn: (name: string) => renameGroup(groupId, name),
    onSuccess: async () => {
      await invalidateGroup()
      showToast({ message: t('toasts.renamed') })
    },
    onError: reportError,
  })

  const invite = useMutation({
    mutationFn: (userIds: string[]) => inviteGroupMembers(groupId, userIds),
    onSuccess: async () => {
      await invalidateGroup()
      showToast({ message: t('toasts.invited') })
    },
    onError: reportError,
  })

  const removeMember = useMutation({
    mutationFn: (userId: string) => removeGroupMember(groupId, userId),
    onSuccess: async () => {
      await invalidateGroup()
    },
    onError: reportError,
  })

  const remove = useMutation({
    mutationFn: () => deleteGroup(groupId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groupKeys.list })
      showToast({ message: t('toasts.deleted') })
      navigate('/groups')
    },
    onError: reportError,
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/groups" />
  }

  if (detail.isPending) {
    return (
      <main data-testid="screen-group-detail">
        <p className="motion-safe:animate-pulse text-ink-soft" role="status">
          {t('common.loading', { ns: 'common' })}
        </p>
      </main>
    )
  }

  const group = detail.data
  if (!group || !group.members) {
    return (
      <main data-testid="screen-group-detail">
        <div className="mx-auto max-w-[900px] px-3 pb-6 sm:px-6">
          <PageHeader back={{ to: '/groups', label: t('list.heading') }} title={t('notFound.title')} />
          <EmptyState description={t('notFound.description')} icon={UsersRound} title={t('notFound.title')} />
        </div>
      </main>
    )
  }

  const isOwner = session?.userId === group.ownerId
  const acceptedMembers: FriendGroupMember[] = group.members.filter((m) => m.status === 'accepted')
  const invitedMembers: FriendGroupMember[] = group.members.filter((m) => m.status === 'invited')
  const roster: RosterPerson[] = [
    ownerRosterPerson(group, t),
    ...acceptedMembers.map((member) => memberRosterPerson(member, t)),
  ]
  const invitedRoster: RosterPerson[] = invitedMembers.map((member) => memberRosterPerson(member, t))
  const alreadyInGroup = new Set([...roster.map((p) => p.userId), ...invitedRoster.map((p) => p.userId)])
  const candidates = (friends.data ?? []).filter((friend) => !alreadyInGroup.has(friend.userId))

  return (
    <main data-testid="screen-group-detail">
      <div className="mx-auto max-w-[900px] space-y-4 px-3 pb-10 sm:px-6">
        <PageHeader
          actions={
            isOwner ? (
              <>
                <button
                  className={neutralCtaClasses}
                  ref={renameButton}
                  type="button"
                  onClick={() => setRenaming(true)}
                >
                  <Pencil aria-hidden className="size-4" /> {t('actions.rename')}
                </button>
                <button
                  className={primaryCtaClasses}
                  ref={inviteButton}
                  type="button"
                  onClick={() => setInviting(true)}
                >
                  <MailPlus aria-hidden className="size-4" /> {t('invite.action')}
                </button>
              </>
            ) : (
              <button
                className={neutralCtaClasses}
                ref={leaveButton}
                type="button"
                onClick={() => setConfirm({ kind: 'leave' })}
              >
                <DoorOpen aria-hidden className="size-4" /> {t('actions.leave')}
              </button>
            )
          }
          back={{ to: '/groups', label: t('list.heading') }}
          description={t('roster.memberCount', { count: roster.length })}
          eyebrow={
            isOwner
              ? t('eyebrow.owner')
              : t('eyebrow.ownedBy', { name: ownerRosterPerson(group, t).displayName })
          }
          title={group.name}
        />

        <SectionCard icon={UsersRound} title={`${t('sections.roster')} · ${roster.length}`}>
          <ul className="space-y-2">
            {roster.map((person) => (
              <li className="rounded-xl border-2 border-ink bg-card px-3 py-2.5" key={person.userId}>
                <IdentityRow
                  avatar={
                    <AvatarDisc
                      emoji={person.avatar?.emoji}
                      imgUrl={avatarImgUrl(person.avatar)}
                      masked={person.masked}
                      seed={person.userId}
                      size="sm"
                    />
                  }
                  displayName={session?.userId === person.userId ? t('roster.you') : person.displayName}
                  meta={
                    person.isOwner && (
                      <Tag icon={Crown} tone="accent">
                        {t('badges.owner')}
                      </Tag>
                    )
                  }
                  actions={
                    isOwner && session?.userId !== person.userId ? (
                      <button
                        className={neutralCtaClasses}
                        type="button"
                        onClick={() => setConfirm({ kind: 'remove', userId: person.userId })}
                      >
                        {t('actions.remove')}
                        <span className="sr-only"> {person.displayName}</span>
                      </button>
                    ) : undefined
                  }
                />
              </li>
            ))}
          </ul>
        </SectionCard>

        {isOwner && (
          <SectionCard icon={MailPlus} title={`${t('sections.invited')} · ${invitedRoster.length}`}>
            {invitedRoster.length === 0 ? (
              <EmptyState
                action={
                  <Button variant="primary" onClick={() => setInviting(true)}>
                    <MailPlus aria-hidden className="size-4" /> {t('invite.action')}
                  </Button>
                }
                description={t('emptyState.invited.body')}
                icon={MailPlus}
                title={t('emptyState.invited.title')}
              />
            ) : (
              <ul className="space-y-2">
                {invitedRoster.map((person) => (
                  <li
                    className="rounded-xl border-2 border-dashed border-ink/50 bg-muted/40 px-3 py-2.5"
                    key={person.userId}
                  >
                    <IdentityRow
                      avatar={
                        <AvatarDisc
                          emoji={person.avatar?.emoji}
                          imgUrl={avatarImgUrl(person.avatar)}
                          masked={person.masked}
                          seed={person.userId}
                          size="sm"
                        />
                      }
                      displayName={person.displayName}
                      meta={<Tag>{t('badges.invited')}</Tag>}
                      actions={
                        <button
                          className={neutralCtaClasses}
                          type="button"
                          onClick={() => removeMember.mutate(person.userId)}
                        >
                          {t('actions.revoke')}
                        </button>
                      }
                    />
                  </li>
                ))}
              </ul>
            )}
          </SectionCard>
        )}

        {isOwner && (
          <section className="surface border-danger/40 p-4">
            <h2 className="font-display text-base font-extrabold text-ink">{t('danger.title')}</h2>
            <p className="mt-1 text-[12.5px] text-ink-soft">{t('danger.description')}</p>
            <Button
              className="mt-3"
              type="button"
              variant="danger"
              onClick={() => setConfirm({ kind: 'delete' })}
            >
              <Trash2 aria-hidden className="size-4" /> {t('danger.action', { group: group.name })}
            </Button>
          </section>
        )}

        <RenameDialog
          currentName={group.name}
          open={renaming}
          triggerRef={renameButton}
          onOpenChange={setRenaming}
          onSave={(name) => rename.mutate(name)}
        />
        <InviteDialog
          candidates={candidates}
          open={inviting}
          triggerRef={inviteButton}
          onInvite={(userIds) => invite.mutate(userIds)}
          onOpenChange={setInviting}
        />
        <ConfirmDialog
          confirm={confirm}
          groupName={group.name}
          memberName={
            confirmMemberId
              ? (roster.find((p) => p.userId === confirmMemberId)?.displayName ??
                invitedRoster.find((p) => p.userId === confirmMemberId)?.displayName ??
                t('member.unknown'))
              : ''
          }
          onConfirm={() => {
            if (confirm?.kind === 'delete') remove.mutate()
            else if (confirm?.kind === 'leave' && session?.userId) removeMember.mutate(session.userId)
            else if (confirm?.kind === 'remove') removeMember.mutate(confirm.userId)
          }}
          onOpenChange={(open) => !open && setConfirm(null)}
        />
      </div>
    </main>
  )
}
