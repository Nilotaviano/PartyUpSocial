import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
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
import { Welcome } from './AuthScreens'

function renderWelcome() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter>
        <Welcome />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(async () => {
  cleanup()
  vi.clearAllMocks()
  await i18n.changeLanguage('en')
})

describe('Welcome sign-in disclosure', () => {
  it('flips aria-expanded on the still-mounted trigger and reveals the panel_F1', () => {
    session.useSession.mockReturnValue({
      data: { providers: ['google', 'steam'] },
      isPending: false,
    })
    renderWelcome()

    const trigger = screen.getByRole('button', { name: 'Sign in' })
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(trigger).toHaveAttribute('aria-controls', 'welcome-signin-panel')
    expect(screen.queryByText('Choose how to sign in')).not.toBeInTheDocument()

    trigger.focus()
    fireEvent.click(trigger)

    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Choose how to sign in')).toBeInTheDocument()
    expect(document.getElementById('welcome-signin-panel')).toBeInTheDocument()
    // Standard disclosure pattern: activating the trigger never unmounts it,
    // so a focus it already held is never lost.
    expect(document.activeElement).toBe(trigger)
  })

  it('renders Google/Steam rows only when the session offers those providers', () => {
    session.useSession.mockReturnValue({
      data: { providers: ['google', 'steam'] },
      isPending: false,
    })
    renderWelcome()
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(
      screen.getByRole('button', { name: 'Continue with Google' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Continue with Steam' }),
    ).toBeInTheDocument()
  })

  it('hides Google/Steam rows when the session offers neither provider', () => {
    session.useSession.mockReturnValue({
      data: { providers: [] },
      isPending: false,
    })
    renderWelcome()
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(
      screen.queryByRole('button', { name: 'Continue with Google' }),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Continue with Steam' }),
    ).not.toBeInTheDocument()
  })

  it('links the email row to /login and Create account to /register', () => {
    session.useSession.mockReturnValue({
      data: { providers: [] },
      isPending: false,
    })
    renderWelcome()
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(screen.getByRole('link', { name: 'Sign in with email' })).toHaveAttribute(
      'href',
      '/login',
    )
    expect(screen.getByRole('link', { name: 'Create account' })).toHaveAttribute(
      'href',
      '/register',
    )
  })
})
