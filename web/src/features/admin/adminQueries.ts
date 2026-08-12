import type { QueryClient } from '@tanstack/react-query'
import { client } from '../../api/client'
import type { components } from '../../api/schema'

export type AdminDashboard = components['schemas']['AdminDashboardResponse']
export type AdminUsageWindow = components['schemas']['AdminUsageWindowResponse']
export type AdminUserSummary = components['schemas']['AdminUserResponse']
export type AdminUsers = components['schemas']['AdminUsersResponse']

// Every integral/double member of the admin schemas is typed `number | string`
// by the generator (OpenAPI int32/double under the `format` mapping), so each
// display site coerces once here rather than trusting the wire shape.
export function numeric(value: number | string): number {
  return typeof value === 'number' ? value : Number(value)
}

export function optionalNumeric(value: null | number | string): number | null {
  return value === null ? null : numeric(value)
}

export const adminKeys = {
  dashboard: ['admin', 'dashboard'] as const,
  users: (query: string, page: number) => ['admin', 'users', query, page] as const,
  user: (userId: string) => ['admin', 'user', userId] as const,
}

export async function getAdminDashboard(): Promise<AdminDashboard> {
  const { data } = await client.GET('/api/v1/admin/dashboard')
  if (!data) throw new Error('Admin dashboard response was empty.')
  return data
}

export async function getAdminUsers(query: string, page: number): Promise<AdminUsers> {
  const { data } = await client.GET('/api/v1/admin/users', {
    params: { query: { query: query || undefined, page } },
  })
  if (!data) throw new Error('Admin user list response was empty.')
  return data
}

export async function getAdminUser(userId: string): Promise<AdminUserSummary> {
  const { data } = await client.GET('/api/v1/admin/users/{id}', {
    params: { path: { id: userId } },
  })
  if (!data) throw new Error('Admin user response was empty.')
  return data
}

const csrf = { 'X-CSRF-TOKEN': '' }

/**
 * D-180: `removeContent` is never surfaced as a checkbox riding the ban
 * control — the two call sites below are two separate user-facing actions,
 * each with its own confirmation, and this is the only place the wire flag is
 * set at all.
 */
export async function banAdminUser(userId: string) {
  await client.POST('/api/v1/admin/users/{id}/ban', {
    params: { header: csrf, path: { id: userId } },
    body: { removeContent: false },
  })
}

export async function banAndRemoveContent(userId: string) {
  await client.POST('/api/v1/admin/users/{id}/ban', {
    params: { header: csrf, path: { id: userId } },
    body: { removeContent: true },
  })
}

export async function unbanAdminUser(userId: string) {
  await client.POST('/api/v1/admin/users/{id}/unban', {
    params: { header: csrf, path: { id: userId } },
  })
}

export async function invalidateAdminUsers(queryClient: QueryClient) {
  await queryClient.invalidateQueries({ queryKey: ['admin'] })
}
