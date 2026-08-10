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
        data: { username: 'friend.one', avatar: { kind: 'Emoji', emoji: '🎲' } },
      })
    throw new Error(`Unexpected GET ${path}`)
  })
}

function openMenu(trigger: HTMLElement) {
  // Radix's DropdownMenuTrigger opens on `pointerdown` (mouse) — a bare `fireEvent.click` never
  // reaches its handler, unlike `Menu.Item`, whose select handler is wired to `onClick`.
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
}

function renderMenu() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/app']}>
        <Routes>
          <Route path="/app" element={<AccountMenu />} />
          <Route path="/settings/account" element={<div data-testid="screen-settings-account" />} />
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

describe('AccountMenu — items (D183)', () => {
  it('the settings item points at /settings/account_D183', async () => {
    mockProfile()
    renderMenu()

    openMenu(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('menuitem', { name: 'Settings' })).toHaveAttribute(
      'href',
      '/settings/account',
    )
  })

  it('opening the menu and activating sign out POSTs the logout mutation and navigates to /welcome_D183', async () => {
    mockProfile()
    renderMenu()

    openMenu(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Sign out' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/logout',
        expect.objectContaining({ params: { header: { 'X-CSRF-TOKEN': '' } } }),
      ),
    )
    expect(await screen.findByTestId('screen-welcome')).toBeInTheDocument()
  })

  it('sign out closes the menu while the request is in flight, so it cannot be fired twice_D183', async () => {
    mockProfile()
    let releaseLogout: (value: { data: undefined }) => void = () => {}
    api.post.mockImplementation(() => new Promise((resolve) => { releaseLogout = resolve }))
    renderMenu()

    openMenu(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Sign out' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))

    await waitFor(() =>
      expect(screen.queryByRole('menuitem', { name: 'Sign out' })).not.toBeInTheDocument(),
    )

    releaseLogout({ data: undefined })
    expect(await screen.findByTestId('screen-welcome')).toBeInTheDocument()
    expect(api.post).toHaveBeenCalledTimes(1)
  })

  it('the keyboard path opens the menu and operates sign out from the keyboard_D183', async () => {
    mockProfile()
    renderMenu()

    const trigger = await screen.findByRole('button', { name: 'Account menu' })
    trigger.focus()
    expect(trigger).toHaveFocus()

    fireEvent.keyDown(trigger, { key: 'Enter', code: 'Enter' })
    const signOutItem = await screen.findByRole('menuitem', { name: 'Sign out' })

    fireEvent.keyDown(signOutItem, { key: 'Enter', code: 'Enter' })

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/logout',
        expect.objectContaining({ params: { header: { 'X-CSRF-TOKEN': '' } } }),
      ),
    )
    expect(await screen.findByTestId('screen-welcome')).toBeInTheDocument()
  })
})
