import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { Crown, Inbox, Plus, UserPlus, UsersRound } from 'lucide-react'
import { useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { useSession } from '../../auth/useSession'
import { EmptyState, SectionCard } from '../../components/shell/SectionCard'
import { PageHeader } from '../../components/shell/PageHeader'
import { Button } from '../../components/ui/button'
import { primaryCtaClasses } from '../../components/ui/classes'
import { useToast } from '../../components/ui/toastStore'
import { friendKeys, getFriends } from '../friends/friendGraph'
import { AvatarStack, Tag, NewGroupDialog } from './GroupBits'
import { identityName, type IdentityLookup } from './identity'
import {
  acceptGroupInvitation,
  createGroup,
  declineGroupInvitation,
  getGroupDetail,
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
 *  - `GET /friend-groups` returns only `{id,name,ownerId,createdAt}` per
 *    group (no member ids, no counts) — the prototype's `Group` type carried
 *    both directly. Each card's member count/avatar stack is filled in by a
 *    second, per-group `GET /friend-groups/{id}` fetch (the same endpoint
 *    the detail page uses), fired for every listed group via `useQueries`;
 *    a card whose detail hasn't resolved yet simply omits the stack/count
 *    line rather than showing a placeholder.
 *  - The prototype's 6-tab bottom bar is not reproduced; Groups reaches the
 *    shell nav through `navItems.ts` like every other section.
 *  - Empty states render through the shell's `EmptyState` (h3 heading).
 */

function useIdentityLookup() {
  const friends = useQuery({ queryKey: friendKeys.friends, queryFn: getFriends })
  const { data: session } = useSession()

  const resolve = useMemo<IdentityLookup>(() => {
    const map = new Map<string, { displayName: string; emoji?: string | null; imgUrl?: string | null }>()
    for (const friend of friends.data ?? []) {
      map.set(friend.userId, {
        displayName: friend.displayName,
        emoji: friend.avatar.emoji,
        imgUrl: friend.avatar.imgUrl,
      })
    }
    if (session?.userId && session.profile) {
      map.set(session.userId, {
        displayName: session.profile.displayName,
        emoji: session.profile.avatar.emoji,
        imgUrl: session.profile.avatar.imgUrl,
      })
    }
    return (userId) => map.get(userId)
  }, [friends.data, session])

  return { resolve, candidates: friends.data ?? [] }
}

type GroupDetailMap = Map<string, FriendGroupDetail | undefined>

function useGroupDetails(groupIds: string[]): GroupDetailMap {
  const pages = useQueries({
    queries: groupIds.map((id) => ({
      queryKey: groupKeys.detail(id),
      queryFn: () => getGroupDetail(id),
    })),
  })
  return new Map(groupIds.map((id, index) => [id, pages[index]?.data]))
}

function rosterFor(group: FriendGroupSummary, detail: FriendGroupDetail | undefined) {
  const members = detail?.members ?? []
  const accepted = [group.ownerId, ...members.filter((m) => m.status === 'accepted').map((m) => m.userId)]
  const invited = members.filter((m) => m.status === 'invited').length
  return { accepted, invited, resolved: detail !== undefined }
}

function GroupCard({
  group,
  owner,
  resolve,
  detail,
}: {
  group: FriendGroupSummary
  owner: boolean
  resolve: IdentityLookup
  detail: FriendGroupDetail | undefined
}) {
  const { t } = useTranslation('groups')
  const roster = rosterFor(group, detail)

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
            {roster.resolved && (
              <span className="mt-0.5 block text-[12px] text-ink-soft tabular-nums">
                {t('roster.memberCount', { count: roster.accepted.length })}
                {owner && roster.invited > 0 && ` · ${t('roster.invitedCount', { count: roster.invited })}`}
                {!owner && ` · ${t('roster.ownedBy', { name: identityName(group.ownerId, resolve, t) })}`}
              </span>
            )}
          </span>
          {owner && (
            <Tag icon={Crown} tone="accent">
              {t('badges.owner')}
            </Tag>
          )}
        </div>
        {roster.resolved && (
          <div className="mt-3">
            <AvatarStack resolve={resolve} userIds={roster.accepted} />
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
  const { resolve, candidates } = useIdentityLookup()

  const owned = list.data?.owned ?? []
  const memberOf = list.data?.memberOf ?? []
  const invites = list.data?.invites ?? []
  const detailById = useGroupDetails([...owned, ...memberOf].map((g) => g.id))

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
                        name: identityName(invite.ownerId, resolve, t),
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
                    detail={detailById.get(group.id)}
                    group={group}
                    key={group.id}
                    owner
                    resolve={resolve}
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
                    detail={detailById.get(group.id)}
                    group={group}
                    key={group.id}
                    owner={false}
                    resolve={resolve}
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
