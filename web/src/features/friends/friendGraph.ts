import type { QueryClient } from '@tanstack/react-query'
import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type FriendItem = components['schemas']['FriendItemResponse']
export type FriendRequestItem =
  components['schemas']['FriendRequestItemResponse']
export type BlockedUser = components['schemas']['BlockedUserResponse']
export type FriendshipResource =
  components['schemas']['FriendshipResourceResponse']
export type SteamImportResult =
  components['schemas']['SteamFriendImportResponse']

export const friendKeys = {
  friends: ['friends'] as const,
  requests: ['friendRequests'] as const,
  profile: (username: string) => ['profile', username] as const,
  friendCode: (friendCode: string) => ['friendCode', friendCode] as const,
  blocks: ['blocks'] as const,
}

export async function getFriends() {
  const { data } = await client.GET('/api/v1/friends')
  if (!data) throw new Error('Friends response was empty.')
  return data.items
}

export async function getFriendRequests() {
  const { data } = await client.GET('/api/v1/friend-requests')
  if (!data) throw new Error('Friend requests response was empty.')
  return data
}

export async function getBlockedUsers() {
  const { data } = await client.GET('/api/v1/blocks')
  if (!data) throw new Error('Blocked users response was empty.')
  return data.items
}

export type FriendGraphAction =
  | {
      kind: 'send'
      username: string
      friendCode?: string
    }
  | {
      kind: 'accept' | 'reject' | 'cancel' | 'unfriend'
      username: string
      friendshipId: string
      friendCode?: string
    }
  | {
      kind: 'block'
      username: string
      friendshipId?: string
      friendCode?: string
    }
  | {
      kind: 'unblock'
      username: string
      blockId: string
    }

export async function runFriendGraphAction(
  action: FriendGraphAction,
): Promise<FriendshipResource | undefined> {
  switch (action.kind) {
    case 'send': {
      const { data } = await client.POST('/api/v1/friend-requests', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: action.username },
      })
      if (!data) throw new Error('Friend request response was empty.')
      return data
    }
    case 'accept': {
      const { data } = await client.POST(
        '/api/v1/friend-requests/{friendshipId}/accept',
        {
          params: {
            header: { 'X-CSRF-TOKEN': '' },
            path: { friendshipId: action.friendshipId },
          },
        },
      )
      if (!data) throw new Error('Friend acceptance response was empty.')
      return data
    }
    case 'reject':
      await client.POST('/api/v1/friend-requests/{friendshipId}/reject', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { friendshipId: action.friendshipId },
        },
      })
      return undefined
    case 'cancel':
      await client.DELETE('/api/v1/friend-requests/{friendshipId}', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { friendshipId: action.friendshipId },
        },
      })
      return undefined
    case 'unfriend':
      await client.DELETE('/api/v1/friends/{friendshipId}', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { friendshipId: action.friendshipId },
        },
      })
      return undefined
    case 'block':
      await client.POST('/api/v1/blocks', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { targetUsername: action.username },
      })
      return undefined
    case 'unblock':
      await client.DELETE('/api/v1/blocks/{blockId}', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { blockId: action.blockId },
        },
      })
      return undefined
  }
}

export async function invalidateFriendGraph(
  queryClient: QueryClient,
  action: FriendGraphAction,
) {
  const invalidations = [
    queryClient.invalidateQueries({
      queryKey: friendKeys.friends,
      exact: true,
    }),
    queryClient.invalidateQueries({
      queryKey: friendKeys.requests,
      exact: true,
    }),
    queryClient.invalidateQueries({
      queryKey: friendKeys.profile(action.username),
      exact: true,
    }),
  ]

  if ('friendCode' in action && action.friendCode) {
    invalidations.push(
      queryClient.invalidateQueries({
        queryKey: friendKeys.friendCode(action.friendCode),
        exact: true,
      }),
    )
  }

  if (action.kind === 'block' || action.kind === 'unblock') {
    invalidations.push(
      queryClient.invalidateQueries({
        queryKey: friendKeys.blocks,
        exact: true,
      }),
    )
  }

  await Promise.all(invalidations)
}
