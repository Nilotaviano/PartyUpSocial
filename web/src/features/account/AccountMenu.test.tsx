import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { AccountMenu } from './AccountMenu'

function mockProfile() {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me')
      return Promise.resolve({
        data: {
          userId: '00000000-0000-0000-0000-000000000001',
          username: 'friend.one',
          displayName: null,
          avatar: { kind: 'Emoji', emoji: '🎲' },
        },
      })
    throw new Error(`Unexpected GET ${path}`)
  })
}

function renderMenu() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/app']}>
        <Routes>
          <Route path="/app" element={<AccountMenu />} />
          <Route path="/settings/account" element={<div data-testid="screen-settings-account" />} />
          <Route path="/settings/profile" element={<div data-testid="screen-settings-profile" />} />
          <Route path="/settings/notifications" element={<div data-testid="screen-settings-notifications" />} />
          <Route path="/welcome" element={<div data-testid="screen-welcome" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.post.mockResolvedValue({ data: undefined })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AccountMenu — trigger (D183)', () => {
  it('carries data-tour="settings" on the trigger so the D-182 tour keeps its eighth anchor_D183', async () => {
    mockProfile()
    renderMenu()

    const trigger = await screen.findByRole('button', { name: 'Account menu' })
    expect(trigger).toHaveAttribute('data-tour', 'settings')
  })

  it('renders the trigger with a neutral avatar while the profile query is still pending_D183', async () => {
    api.get.mockImplementation(() => new Promise(() => {}))
    renderMenu()

    expect(screen.getByRole('button', { name: 'Account menu' })).toBeInTheDocument()
  })
})

describe('AccountMenu — sheet panel (D183, ported to the shell Sheet grammar)', () => {
  it('opens on click and shows the viewer name in the header', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('friend.one')).toBeInTheDocument()
  })

  it('the settings row is a link to /settings/account_D183', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('link', { name: 'Settings' })).toHaveAttribute('href', '/settings/account')
  })

  it('the profile row is a link to /settings/profile', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('link', { name: 'Profile' })).toHaveAttribute('href', '/settings/profile')
  })

  it('the privacy row is a link to /settings/notifications', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('link', { name: 'Privacy & notifications' })).toHaveAttribute(
      'href',
      '/settings/notifications',
    )
  })

  it('Esc closes the panel', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    await screen.findByRole('dialog')

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('activating sign out POSTs the logout mutation and navigates to /welcome_D183', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/logout',
        expect.objectContaining({ params: { header: { 'X-CSRF-TOKEN': '' } } }),
      ),
    )
    expect(await screen.findByTestId('screen-welcome')).toBeInTheDocument()
  })

  it('sign out closes the panel while the request is in flight, so it cannot be fired twice_D183', async () => {
    mockProfile()
    let releaseLogout: (value: { data: undefined }) => void = () => {}
    api.post.mockImplementation(() => new Promise((resolve) => { releaseLogout = resolve }))
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    releaseLogout({ data: undefined })
    expect(await screen.findByTestId('screen-welcome')).toBeInTheDocument()
    expect(api.post).toHaveBeenCalledTimes(1)
  })
})
