import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => {
  class ApiError extends Error {
    readonly status: number
    readonly code: string
    readonly errors: Record<string, readonly string[]>

    constructor(status: number, code: string, errors: Record<string, readonly string[]> = {}) {
      super(code)
      this.name = 'ApiError'
      this.status = status
      this.code = code
      this.errors = errors
    }
  }
  return { get: vi.fn(), remove: vi.fn(), ApiError }
})

vi.mock('../../api/client', () => ({
  client: { GET: api.get, DELETE: api.remove },
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))

import '../../i18n'
import { ProfileSettingsPage } from './ProfileSettingsPage'

function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><ProfileSettingsPage /></MemoryRouter></QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

describe('ProfileSettingsPage', () => {
  it('retries a Steam platform revoke with A-049 proof and renders the last-method API error_D137', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: 'game', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [{ id: 'steam-link', platform: 'Steam', handle: 'player' }],
        } })
      }
      return Promise.resolve({ data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] } })
    })
    api.remove
      .mockRejectedValueOnce(new api.ApiError(409, 'reauth_required'))
      .mockRejectedValueOnce(new api.ApiError(409, 'last_auth_method'))

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Unlink Steam' }))
    expect(await screen.findByRole('dialog')).toBeVisible()
    expect(api.remove).toHaveBeenCalledWith('/api/v1/me/platform-links/{id}', expect.objectContaining({ params: expect.objectContaining({ path: { id: 'steam-link' } }) }))

    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'correct-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirm with password' }))

    await waitFor(() => expect(api.remove).toHaveBeenLastCalledWith('/api/v1/me/platform-links/{id}', expect.objectContaining({ body: { currentPassword: 'correct-password', reauthToken: null } })))
    expect(await screen.findByRole('alert')).toHaveTextContent('You need at least one sign-in method.')
  })
})