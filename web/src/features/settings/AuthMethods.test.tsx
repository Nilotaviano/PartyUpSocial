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
  return { get: vi.fn(), post: vi.fn(), remove: vi.fn(), ApiError }
})
const session = vi.hoisted(() => ({ useSession: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, DELETE: api.remove },
  ApiError: api.ApiError,
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))
vi.mock('../../auth/externalIntent', () => ({ startExternalProviderWithRetry: (mintIntent: () => Promise<unknown>) => mintIntent() }))
vi.mock('../../auth/useSession', () => ({ useSession: session.useSession, sessionQueryKey: ['session'] }))

import '../../i18n'
import { AccountSettingsPage } from './AccountSettingsPage'

function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

describe('AuthMethods', () => {
  it('shows the explicit API error when unlinking the last method_D137', async () => {
    session.useSession.mockReturnValue({ data: { profile: null, providers: [] } })
    api.get.mockResolvedValue({ data: { methods: [
      { kind: 'password', linked: true, removable: false, detail: 'Password' },
      { kind: 'google', linked: true, removable: true, detail: 'Google' },
    ] } })
    api.remove.mockRejectedValue(new api.ApiError(409, 'last_auth_method'))

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Unlink google' }))
    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'correct-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirm with password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('You need at least one sign-in method.')
  })

  it('renders link buttons only for configured, unlinked providers and posts a link intent_D087', async () => {
    session.useSession.mockReturnValue({ data: { profile: null, providers: ['google', 'steam', 'discord'] } })
    api.get.mockResolvedValue({ data: { methods: [
      { kind: 'google', linked: true, removable: true, detail: 'Google' },
      { kind: 'steam', linked: false, removable: false, detail: 'Steam' },
    ] } })
    api.post.mockResolvedValue({ data: { intent: 'link-intent' } })

    renderPage()

    expect(await screen.findByRole('button', { name: 'Link Steam' })).toBeVisible()
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Link Google' })).not.toBeInTheDocument())
    expect(screen.queryByRole('button', { name: /discord/i })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Link Steam' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/v1/auth/external/intent', expect.objectContaining({ body: expect.objectContaining({ provider: 'steam', mode: 'link' }) })))
  })
})