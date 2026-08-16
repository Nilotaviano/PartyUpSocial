import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))
const session = vi.hoisted(() => ({ useSession: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  ApiError: class extends Error {},
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))
vi.mock('../../auth/externalIntent', () => ({
  startExternalProviderWithRetry: async (
    mintIntent: () => Promise<unknown>,
  ) => {
    await mintIntent()
  },
}))
vi.mock('../../auth/useSession', () => ({
  useSession: session.useSession,
  sessionQueryKey: ['session'],
}))

import '../../i18n'
import i18n from '../../i18n'
import { Login } from './AuthScreens'

function renderLogin() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <Login />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(async () => {
  cleanup()
  sessionStorage.clear()
  vi.clearAllMocks()
  await i18n.changeLanguage('en')
})

describe('external login', () => {
  it.each([
    ['Google', 'google'],
    ['Steam', 'steam'],
  ])('mints a login intent before starting %s_D067', async (label, provider) => {
    session.useSession.mockReturnValue({
      data: { providers: ['google', 'steam'] },
      isPending: false,
    })
    api.post.mockResolvedValue({ data: { intent: `${provider}-intent` } })
    renderLogin()

    fireEvent.click(
      screen.getByRole('button', { name: `Continue with ${label}` }),
    )

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/external/intent',
        {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: {
            provider,
            mode: 'login',
            returnTo: null,
            ageGateAttested: false,
            currentPassword: null,
            reauthToken: null,
          },
        },
      ),
    )
  })

  it('disables the Steam button and marks it aria-busy while the intent request is in flight (Wave 6)', async () => {
    session.useSession.mockReturnValue({
      data: { providers: ['google', 'steam'] },
      isPending: false,
    })
    api.post.mockImplementation(() => new Promise(() => {}))
    renderLogin()

    const steamButton = screen.getByRole('button', { name: 'Continue with Steam' })
    fireEvent.click(steamButton)

    await waitFor(() => expect(steamButton).toBeDisabled())
    expect(steamButton).toHaveAttribute('aria-busy', 'true')
    // The Google row is a separate pending flag — it isn't disabled by Steam's request.
    expect(
      screen.getByRole('button', { name: 'Continue with Google' }),
    ).not.toBeDisabled()
  })

  it('carries an age-gate attestation and continuation into a relaunched login intent_D083', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    session.useSession.mockReturnValue({
      data: { providers: ['steam'] },
      isPending: false,
    })
    api.post.mockResolvedValue({ data: { intent: 'steam-intent' } })
    render(
      <QueryClientProvider
        client={
          new QueryClient({ defaultOptions: { queries: { retry: false } } })
        }
      >
        <MemoryRouter initialEntries={['/login?next=%2Fadd%2FXQYENU8937']}>
          <Routes>
            <Route path="/login" element={<Login />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Continue with Steam' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/auth/external/intent', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          provider: 'steam',
          mode: 'login',
          returnTo: '/add/XQYENU8937',
          ageGateAttested: true,
          currentPassword: null,
          reauthToken: null,
        },
      }),
    )
  })
})
