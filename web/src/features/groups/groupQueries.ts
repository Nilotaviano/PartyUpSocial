import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type FriendGroupSummary = components['schemas']['FriendGroupResponse']
export type FriendGroupInvite = components['schemas']['FriendGroupInviteResponse']
export type FriendGroupList = components['schemas']['FriendGroupListResponse']
export type FriendGroupDetail = components['schemas']['FriendGroupDetailResponse']
export type FriendGroupMember = components['schemas']['FriendGroupMemberResponse']

export const groupKeys = {
  list: ['friend-groups'] as const,
  detail: (id: string) => ['friend-group', id] as const,
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getGroupList() {
  const { data } = await client.GET('/api/v1/friend-groups')
  if (!data) throw new Error('Friend group list response was empty.')
  return data
}

export async function getGroupDetail(groupId: string) {
  const { data } = await client.GET('/api/v1/friend-groups/{groupId}', {
    params: { path: { groupId } },
  })
  if (!data) throw new Error('Friend group detail response was empty.')
  return data
}

export async function createGroup(name: string) {
  const { data } = await client.POST('/api/v1/friend-groups', {
    params: { header: csrf },
    body: { name },
  })
  if (!data) throw new Error('Create friend group response was empty.')
  return data
}

export async function renameGroup(groupId: string, name: string) {
  const { data } = await client.PATCH('/api/v1/friend-groups/{groupId}', {
    params: { header: csrf, path: { groupId } },
    body: { name },
  })
  if (!data) throw new Error('Rename friend group response was empty.')
  return data
}

export async function deleteGroup(groupId: string) {
  await client.DELETE('/api/v1/friend-groups/{groupId}', {
    params: { header: csrf, path: { groupId } },
  })
}

export async function inviteGroupMembers(groupId: string, userIds: string[]) {
  const { data } = await client.POST('/api/v1/friend-groups/{groupId}/members', {
    params: { header: csrf, path: { groupId } },
    body: { userIds },
  })
  if (!data) throw new Error('Invite friend group members response was empty.')
  return data
}

/** Owner path (removing another member) and self path (leaving) share this one route. */
export async function removeGroupMember(groupId: string, userId: string) {
  await client.DELETE('/api/v1/friend-groups/{groupId}/members/{userId}', {
    params: { header: csrf, path: { groupId, userId } },
  })
}

export async function acceptGroupInvitation(groupId: string) {
  const { data } = await client.POST('/api/v1/friend-groups/{groupId}/invitation/accept', {
    params: { header: csrf, path: { groupId } },
  })
  if (!data) throw new Error('Accept friend group invitation response was empty.')
  return data
}

export async function declineGroupInvitation(groupId: string) {
  await client.POST('/api/v1/friend-groups/{groupId}/invitation/decline', {
    params: { header: csrf, path: { groupId } },
  })
}
