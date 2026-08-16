import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const useSessionMock = vi.hoisted(() => vi.fn())
vi.mock('../../api/client', () => ({ client: { GET: api.get, POST: api.post }, isApiError: () => false }))
vi.mock('../../auth/useSession', () => ({ useSession: useSessionMock, sessionQueryKey: ['session'] }))

import '../../i18n'
import { setReauthProof } from '../../auth/reauthProof'
import { AccountSettingsPage } from './AccountSettingsPage'
import { getPendingSettingsAction, setPendingSettingsAction } from './reauthAction'

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return { queryClient, ...render(<QueryClientProvider client={queryClient}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>) }
}

beforeEach(() => {
  useSessionMock.mockReturnValue({ data: { profile: null, providers: [] } })
})

afterEach(() => {
  cleanup()
  sessionStorage.clear()
  vi.clearAllMocks()
})

describe('DeleteAccount', () => {
  it('states the seven-day grace and login-cancels rule before confirming_D148_D158', () => {
    api.get.mockResolvedValue({ data: { methods: [] } })
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: 'Delete my account' }))

    expect(screen.getByRole('dialog')).toHaveTextContent('Deletion starts a 7-day grace period before destructive effects occur.')
    expect(screen.getByRole('dialog')).toHaveTextContent('Signing in during that grace period cancels the deletion request.')
  })

  it('resumes a stored action after navigation, spends its in-memory proof once, and posts the deletion_D148', async () => {
    api.get.mockResolvedValue({ data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] } })
    api.post.mockResolvedValue({ data: { graceEndsAt: '2026-08-08T00:00:00Z' } })
    setPendingSettingsAction({ kind: 'deleteAccount' })
    setReauthProof('proof-from-callback')

    const { queryClient } = renderPage()
    const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries')

    expect(getPendingSettingsAction()).toBeNull()
    fireEvent.click(await screen.findByRole('button', { name: 'Continue with confirmed identity' }))

    expect(api.post).toHaveBeenCalledTimes(1)
    expect(api.post).toHaveBeenCalledWith('/api/v1/me/deletion', expect.objectContaining({ body: { currentPassword: null, reauthToken: 'proof-from-callback' } }))
    const expected = new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date('2026-08-08T00:00:00Z'))
    expect(await screen.findByRole('status')).toHaveTextContent(expected)
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['session'] }))
    expect(getPendingSettingsAction()).toBeNull()
  })

  it('renders the deletion grace deadline in the viewer profile time zone rather than the browser zone_D038_D046', async () => {
    const graceEndsAt = '2026-08-08T00:00:00Z'
    const format = (timeZone?: string) =>
      new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(
        new Date(graceEndsAt),
      )
    const hostRender = format()
    // A profile zone whose rendering provably differs from the host zone, so the
    // assertion fails if the component silently falls back to the browser zone.
    const profileZone = ['Pacific/Midway', 'Pacific/Kiritimati'].find(
      (zone) => format(zone) !== hostRender,
    )!
    useSessionMock.mockReturnValue({ data: { profile: { timeZone: profileZone }, providers: [] } })
    api.get.mockResolvedValue({ data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] } })
    api.post.mockResolvedValue({ data: { graceEndsAt } })
    setPendingSettingsAction({ kind: 'deleteAccount' })
    setReauthProof('proof-from-callback')

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Continue with confirmed identity' }))

    expect(await screen.findByRole('status')).toHaveTextContent(format(profileZone))
  })
})