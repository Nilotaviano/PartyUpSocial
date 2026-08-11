import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
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
  return { get: vi.fn(), remove: vi.fn(), put: vi.fn(), ApiError }
})

vi.mock('../../api/client', () => ({
  client: { GET: api.get, DELETE: api.remove, PUT: api.put },
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))

import '../../i18n'
import { setReauthProof } from '../../auth/reauthProof'
import { getPendingSettingsAction, setPendingSettingsAction } from '../settings/reauthAction'
import { ProfileSettingsPage } from './ProfileSettingsPage'

function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><ProfileSettingsPage /></MemoryRouter></QueryClientProvider>)
}

afterEach(() => {
  cleanup()
  sessionStorage.clear()
  vi.clearAllMocks()
})

describe('ProfileSettingsPage', () => {
  it('retries a Steam platform revoke with A-049 proof and renders the last-method API error_D137', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
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
  it('resumes a persisted platform unlink with an arriving proof once', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [{ id: 'steam-link', platform: 'Steam', handle: 'player' }],
        } })
      }
      return Promise.resolve({ data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] } })
    })
    api.remove.mockResolvedValue({ data: {} })
    setPendingSettingsAction({ kind: 'unlinkPlatform', id: 'steam-link' })
    setReauthProof('proof-from-callback')

    renderPage()

    await screen.findByRole('button', { name: 'Continue with confirmed identity' })
    expect(getPendingSettingsAction()).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Continue with confirmed identity' }))

    await waitFor(() => expect(api.remove).toHaveBeenCalledWith('/api/v1/me/platform-links/{id}', expect.objectContaining({
      params: expect.objectContaining({ path: { id: 'steam-link' } }),
      body: { currentPassword: null, reauthToken: 'proof-from-callback' },
    })))
    expect(api.remove).toHaveBeenCalledTimes(1)
    expect(getPendingSettingsAction()).toBeNull()
  })

  it('blocks submission of a typed time zone that is not a real IANA zone, without calling the API', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [],
        } })
      }
      return Promise.resolve({ data: { methods: [] } })
    })

    renderPage()

    const field = await screen.findByLabelText('Time zone')
    fireEvent.change(field, { target: { value: 'Not/A_Real_Zone' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save time zone' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a time zone from the list.')
    expect(api.put).not.toHaveBeenCalled()
  })

  it('accepts and saves a real IANA zone typed into the time zone field, keeping the account current stored zone valid too', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [],
        } })
      }
      return Promise.resolve({ data: { methods: [] } })
    })
    api.put.mockResolvedValue({ data: {} })

    renderPage()

    const field = await screen.findByLabelText('Time zone')
    expect(field).toHaveValue('America/Fortaleza')
    fireEvent.change(field, { target: { value: 'America/Sao_Paulo' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save time zone' }))

    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/v1/me/time-zone', expect.objectContaining({
      body: { timeZone: 'America/Sao_Paulo' },
    })))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('saves a catalog glyph chosen in the avatar picker through the existing form path', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [],
        } })
      }
      return Promise.resolve({ data: { methods: [] } })
    })
    api.put.mockResolvedValue({ data: {} })

    renderPage()

    const group = await screen.findByRole('radiogroup', { name: 'Avatar' })
    expect(group).toHaveAttribute('aria-labelledby', 'profile-avatar-label')
    expect(screen.getByRole('radio', { name: 'Game controller' })).toHaveAttribute('aria-checked', 'true')
    expect(screen.queryByRole('textbox', { name: 'Avatar' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: 'Dragon' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save avatar' }))

    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/v1/me/avatar', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { kind: 'emoji', emoji: '🐉', platformLinkId: null },
    }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})