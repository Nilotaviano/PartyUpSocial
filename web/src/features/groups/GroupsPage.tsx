import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Crown, Inbox, Plus, UserPlus, UsersRound } from 'lucide-react'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { EmptyState, SectionCard } from '../../components/shell/SectionCard'
import { PageHeader } from '../../components/shell/PageHeader'
import { Button } from '../../components/ui/button'
import { primaryCtaClasses } from '../../components/ui/classes'
import { useToast } from '../../components/ui/toastStore'
import { friendKeys, getFriends } from '../friends/friendGraph'
import { AvatarStack, Tag, NewGroupDialog, type StackPerson } from './GroupBits'
import {
  acceptGroupInvitation,
  createGroup,
  declineGroupInvitation,
  getGroupList,
  groupKeys,
  inviteGroupMembers,
  type FriendGroupDetail,
  type FriendGroupSummary,
} from './groupQueries'

/**
 * Ported from the Lovable reference (`src/routes/groups.index.tsx`).
 * Adaptations:
 *  - The prototype's in-memory `useGroups()` store is replaced by TanStack
 *    Query against the real API.
 *  - `GET /friend-groups` returns `memberCount` (owner + every Accepted
 *    member) and the owner's nameplate (`ownerDisplayName`/`ownerAvatar`/
 *    `ownerMasked`, D-076 masking already applied server-side) directly on
 *    each card's summary and on each pending invite — no per-group detail
 *    fetch or client-side friends-list join needed for either. A card's
 *    avatar stack renders only from a group's detail query that is ALREADY
 *    in the TanStack Query cache (the viewer has visited that group's
 *    detail page this session) — read passively via
 *    `queryClient.getQueryData`, never fetched here, so listing groups
 *    never fires the N+1 per-card request the count alone used to require.
 *  - The prototype's 6-tab bottom bar is not reproduced; Groups reaches the
 *    shell nav through `navItems.ts` like every other section.
 *  - Empty states render through the shell's `EmptyState` (h3 heading).
 */

/** Friends candidates for the "New group" dialog's invite step — nothing else needs the friends list. */
function useFriendCandidates() {
  const friends = useQuery({ queryKey: friendKeys.friends, queryFn: getFriends })
  return friends.data ?? []
}

/** The stack people for a card, read passively from an already-cached detail — never fetched here. */
function stackPeopleFrom(detail: FriendGroupDetail | undefined): StackPerson[] | undefined {
  if (!detail || !detail.members) {
    return undefined
  }

  return [
    { userId: detail.ownerId, masked: detail.ownerMasked, avatar: detail.ownerAvatar },
    ...detail.members
      .filter((member) => member.status === 'accepted')
      .map((member) => ({ userId: member.userId, masked: member.masked, avatar: member.avatar })),
  ]
}

function GroupCard({
  group,
  owner,
  stackPeople,
}: {
  group: FriendGroupSummary
  owner: boolean
  stackPeople: StackPerson[] | undefined
}) {
  const { t } = useTranslation('groups')

  return (
    <li>
      <Link
        className="surface block h-full p-3 transition-colors hover:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        to={`/groups/${group.id}`}
      >
        <div className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-2">
          <span className="min-w-0">
            <span className="block truncate font-display text-[15px] font-extrabold text-ink">
              {group.name}
            </span>
            <span className="mt-0.5 block text-[12px] text-ink-soft tabular-nums">
              {t('roster.memberCount', { count: group.memberCount })}
            </span>
            {!owner && (
              <span className="mt-0.5 block truncate text-[12px] text-ink-soft">
                {t('roster.ownedBy', {
                  name: group.ownerMasked
                    ? t('member.anonymous')
                    : (group.ownerDisplayName ?? t('member.unknown')),
                })}
              </span>
            )}
          </span>
          {owner && (
            <Tag icon={Crown} tone="accent">
              {t('badges.owner')}
            </Tag>
          )}
        </div>
        {stackPeople && (
          <div className="mt-3">
            <AvatarStack people={stackPeople} />
          </div>
        )}
      </Link>
    </li>
  )
}

export function GroupsPage() {
  const { t } = useTranslation(['groups', 'common'])
  const queryClient = useQueryClient()
  const showToast = useToast()
  const [dialogOpen, setDialogOpen] = useState(false)
  const newGroupButton = useRef<HTMLButtonElement>(null)

  const list = useQuery({ queryKey: groupKeys.list, queryFn: getGroupList })
  const candidates = useFriendCandidates()

  const owned = list.data?.owned ?? []
  const memberOf = list.data?.memberOf ?? []
  const invites = list.data?.invites ?? []

  function reportError(error: unknown) {
    showToast({
      message: isApiError(error)
        ? t(`errors.codes.${error.code}`, { defaultValue: t('errors.generic') })
        : t('errors.generic'),
    })
  }

  const create = useMutation({
    mutationFn: async ({ name, memberIds }: { name: string; memberIds: string[] }) => {
      const group = await createGroup(name)
      if (memberIds.length > 0) {
        await inviteGroupMembers(group.id, memberIds)
      }
      return group
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groupKeys.list })
      showToast({ message: t('toasts.created') })
    },
    onError: reportError,
  })

  const accept = useMutation({
    mutationFn: (groupId: string) => acceptGroupInvitation(groupId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groupKeys.list })
    },
    onError: reportError,
  })

  const decline = useMutation({
    mutationFn: (groupId: string) => declineGroupInvitation(groupId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groupKeys.list })
    },
    onError: reportError,
  })

  return (
    <main className="min-h-screen bg-background pb-24" data-testid="screen-groups">
      <div className="mx-auto max-w-[900px] px-3 pb-6 sm:px-6">
        <PageHeader
          actions={
            <button
              className={primaryCtaClasses}
              ref={newGroupButton}
              type="button"
              onClick={() => setDialogOpen(true)}
            >
              <Plus aria-hidden className="size-4" />
              {t('list.newGroup')}
            </button>
          }
          description={t('list.description')}
          title={t('list.heading')}
        />

        {list.isPending && (
          <p className="motion-safe:animate-pulse text-ink-soft" role="status">
            {t('common.loading', { ns: 'common' })}
          </p>
        )}

        <div className="flex flex-col gap-4">
          {invites.length > 0 && (
            <SectionCard icon={Inbox} title={`${t('sections.invites')} · ${invites.length}`}>
              <ul className="space-y-2">
                {invites.map((invite) => (
                  <li className="rounded-xl border-2 border-ink bg-accent/40 p-3" key={invite.id}>
                    <p className="text-[13.5px] text-ink">
                      {t('invite.received', {
                        name: invite.ownerMasked
                          ? t('member.anonymous')
                          : (invite.ownerDisplayName ?? t('member.unknown')),
                        group: invite.name,
                      })}
                    </p>
                    <div className="mt-3 flex gap-2">
                      <Button
                        aria-busy={accept.isPending}
                        className="flex-1"
                        disabled={accept.isPending}
                        variant="primary"
                        onClick={() => accept.mutate(invite.id)}
                      >
                        {t('invite.accept')}
                      </Button>
                      <Button
                        aria-busy={decline.isPending}
                        className="flex-1"
                        disabled={decline.isPending}
                        variant="neutral"
                        onClick={() => decline.mutate(invite.id)}
                      >
                        {t('invite.decline')}
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            </SectionCard>
          )}

          <SectionCard icon={Crown} title={`${t('sections.myGroups')} · ${owned.length}`}>
            {owned.length === 0 ? (
              <EmptyState
                action={
                  <Button variant="primary" onClick={() => setDialogOpen(true)}>
                    <Plus aria-hidden className="size-4" />
                    {t('list.newGroup')}
                  </Button>
                }
                description={t('emptyState.owned.body')}
                icon={UsersRound}
                title={t('emptyState.owned.title')}
              />
            ) : (
              <ul className="grid gap-2 sm:grid-cols-2">
                {owned.map((group) => (
                  <GroupCard
                    group={group}
                    key={group.id}
                    owner
                    stackPeople={stackPeopleFrom(
                      queryClient.getQueryData<FriendGroupDetail>(groupKeys.detail(group.id)),
                    )}
                  />
                ))}
              </ul>
            )}
          </SectionCard>

          <SectionCard icon={UsersRound} title={`${t('sections.memberOf')} · ${memberOf.length}`}>
            {memberOf.length === 0 ? (
              <EmptyState
                description={t('emptyState.memberOf.body')}
                icon={UserPlus}
                title={t('emptyState.memberOf.title')}
              />
            ) : (
              <ul className="grid gap-2 sm:grid-cols-2">
                {memberOf.map((group) => (
                  <GroupCard
                    group={group}
                    key={group.id}
                    owner={false}
                    stackPeople={stackPeopleFrom(
                      queryClient.getQueryData<FriendGroupDetail>(groupKeys.detail(group.id)),
                    )}
                  />
                ))}
              </ul>
            )}
          </SectionCard>
        </div>
      </div>

      <NewGroupDialog
        candidates={candidates}
        open={dialogOpen}
        triggerRef={newGroupButton}
        onCreate={(name, memberIds) => create.mutate({ name, memberIds })}
        onOpenChange={setDialogOpen}
      />
    </main>
  )
}
