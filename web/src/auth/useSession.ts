import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { client, refreshAntiforgeryToken, type Session } from '../api/client'
import type { components } from '../api/schema'

export const sessionQueryKey = ['session'] as const

export async function getSession(): Promise<Session> {
  const { data } = await client.GET('/api/v1/auth/session')
  if (!data) {
    throw new Error('Session response was empty.')
  }

  return data
}

export function useSession() {
  return useQuery({
    queryKey: sessionQueryKey,
    queryFn: getSession,
    staleTime: 30_000,
  })
}

function useIdentityMutation<TVariables>(mutationFn: (variables: TVariables) => Promise<unknown>) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn,
    onSuccess: async () => {
      await refreshAntiforgeryToken()
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
  })
}

export function useLoginMutation() {
  return useIdentityMutation((body: components['schemas']['LoginRequest']) =>
    client.POST('/api/v1/auth/login', { params: { header: { 'X-CSRF-TOKEN': '' } }, body }),
  )
}

export function useLogoutMutation() {
  return useIdentityMutation(() => client.POST('/api/v1/auth/logout', { params: { header: { 'X-CSRF-TOKEN': '' } } }))
}

export function useCompleteOnboardingMutation() {
  return useIdentityMutation((body: components['schemas']['ProfileDraft']) =>
    client.POST('/api/v1/onboarding/complete', { params: { header: { 'X-CSRF-TOKEN': '' } }, body }),
  )
}

export function useRequestDeletionMutation() {
  return useIdentityMutation(() => client.POST('/api/v1/me/deletion', { params: { header: { 'X-CSRF-TOKEN': '' } } }))
}
