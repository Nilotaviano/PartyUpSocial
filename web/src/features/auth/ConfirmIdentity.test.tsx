import { render, screen, waitFor } from '@testing-library/react'
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
import { ConfirmIdentity } from './AuthScreens'

function Location() {
  const location = useLocation()
  return <output data-testid="location">{location.pathname}:{JSON.stringify(location.state)}</output>
}

afterEach(() => {
  consumeReauthProof()
  sessionStorage.clear()
  window.history.replaceState({}, '', '/')
  vi.clearAllMocks()
})

describe('ConfirmIdentity', () => {
  it('stores a fresh-tab proof, navigates to account settings, and does not fire an action', async () => {
    window.history.replaceState({}, '', '/auth/confirm-identity#token=fresh-proof')

    render(<MemoryRouter initialEntries={['/auth/confirm-identity']}><ConfirmIdentity /><Location /></MemoryRouter>)

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/settings/account:{"identityConfirmed":true}'))
    expect(consumeReauthProof()).toBe('fresh-proof')
    expect(api.get).not.toHaveBeenCalled()
    expect(api.post).not.toHaveBeenCalled()
  })
})