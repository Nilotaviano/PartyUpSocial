import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock('../../api/client', () => ({ client: { GET: api.get, POST: api.post }, isApiError: () => false }))
vi.mock('../../auth/useSession', () => ({ useSession: vi.fn().mockReturnValue({ data: { profile: null, providers: [] } }), sessionQueryKey: ['session'] }))

import '../../i18n'
import { setReauthProof } from '../../auth/reauthProof'
import { AccountSettingsPage } from './AccountSettingsPage'
import { getPendingSettingsAction, setPendingSettingsAction } from './reauthAction'

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return { queryClient, ...render(<QueryClientProvider client={queryClient}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>) }
}

afterEach(() => {
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
    expect(await screen.findByRole('status')).toHaveTextContent('2026-08-08T00:00:00Z')
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['session'] }))
    expect(getPendingSettingsAction()).toBeNull()
  })
})