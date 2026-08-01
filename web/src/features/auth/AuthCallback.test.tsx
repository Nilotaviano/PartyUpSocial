import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  ApiError: class ApiError extends Error {},
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { consumeReauthProof } from '../../auth/reauthProof'
import { setPendingSettingsAction } from '../settings/reauthAction'
import { AuthCallback } from './AuthScreens'

function Location() {
  const location = useLocation()
  return <output data-testid="location">{location.pathname}</output>
}

function renderCallback(search: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[`/auth/callback${search}`]}>
        <AuthCallback />
        <Location />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  consumeReauthProof()
  sessionStorage.clear()
  window.history.replaceState({}, '', '/')
  vi.clearAllMocks()
})

describe('AuthCallback', () => {
  it('routes a reauth return with a pending platform unlink to profile settings even when next points at account', async () => {
    api.get.mockResolvedValue({ data: { authenticated: true, userId: 'u1', providers: [], state: 'active', isAdmin: false, onboarding: null, pendingDeletion: null, profile: null } })
    setPendingSettingsAction({ kind: 'unlinkPlatform', id: 'link-1' })
    window.history.replaceState({}, '', '/auth/callback?next=%2Fsettings%2Faccount#reauth=proof-1')

    renderCallback('?next=%2Fsettings%2Faccount')

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/settings/profile'))
    expect(consumeReauthProof()).toBe('proof-1')
  })

  it('still honors next for a plain authentication return', async () => {
    api.get.mockResolvedValue({ data: { authenticated: true, userId: 'u1', providers: [], state: 'active', isAdmin: false, onboarding: null, pendingDeletion: null, profile: null } })
    window.history.replaceState({}, '', '/auth/callback?next=%2Fapp')

    renderCallback('?next=%2Fapp')

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/app'))
  })
})
