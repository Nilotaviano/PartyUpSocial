import type { QueryClient } from '@tanstack/react-query'
import { client } from '../../api/client'
import type { components } from '../../api/schema'

export type FeedbackType = 'bug' | 'idea' | 'other'
export type AdminFeedbackItem = components['schemas']['AdminFeedbackResponse']
export type AdminFeedbackList = components['schemas']['AdminFeedbackListResponse']

export const FEEDBACK_TYPES: readonly FeedbackType[] = ['bug', 'idea', 'other']

/** D-212's server-side cap, mirrored here so the field can stop the caller before the round trip. */
export const FEEDBACK_MESSAGE_LIMIT = 4000

/**
 * D-212's auto-attached context. Every field is optional server-side, so a caller that can
 * resolve none of them still submits successfully — each one is omitted rather than sent empty.
 */
export type FeedbackContext = {
  route?: string
  userAgent?: string
  locale?: string
  appVersion?: string
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function submitFeedback(
  type: FeedbackType,
  message: string,
  context: FeedbackContext,
) {
  const { data } = await client.POST('/api/v1/feedback', {
    params: { header: csrf },
    body: {
      type,
      message,
      route: context.route ?? null,
      userAgent: context.userAgent ?? null,
      locale: context.locale ?? null,
      appVersion: context.appVersion ?? null,
    },
  })
  return data
}

export const adminFeedbackKeys = {
  list: (type: FeedbackType | 'all', handled: 'all' | 'handled' | 'unhandled', page: number) =>
    ['admin', 'feedback', type, handled, page] as const,
}

export async function getAdminFeedback(
  type: FeedbackType | 'all',
  handled: 'all' | 'handled' | 'unhandled',
  page: number,
): Promise<AdminFeedbackList> {
  const { data } = await client.GET('/api/v1/admin/feedback', {
    params: {
      query: {
        type: type === 'all' ? undefined : type,
        handled: handled === 'all' ? undefined : handled === 'handled',
        page,
      },
    },
  })
  if (!data) throw new Error('Admin feedback list response was empty.')
  return data
}

export async function setAdminFeedbackHandled(id: string, handled: boolean) {
  await client.POST('/api/v1/admin/feedback/{id}/handled', {
    params: { header: csrf, path: { id } },
    body: { handled },
  })
}

export async function invalidateAdminFeedback(queryClient: QueryClient) {
  await queryClient.invalidateQueries({ queryKey: ['admin'] })
}
