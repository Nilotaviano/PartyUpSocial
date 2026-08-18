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
  it('carries data-tour="settings" on the trigger so the D-182 tour keeps its final anchor_D183', async () => {
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

  it('the keyboard path opens the panel and operates sign out from the keyboard_D183', async () => {
    // Both the trigger and the sign-out row are native <button> elements, so real browsers
    // activate them on Enter/Space via built-in UA semantics with no JS keydown handler
    // involved — jsdom does not implement that default action (a known jsdom gap; see
    // https://github.com/jsdom/jsdom/issues/1026), so the click that a real browser would
    // fire is dispatched explicitly here alongside the keydown, and the assertions cover
    // that dispatching the keydown never gets prevented plus that focus lands correctly.
    mockProfile()
    renderMenu()

    const trigger = await screen.findByRole('button', { name: 'Account menu' })
    trigger.focus()
    expect(trigger).toHaveFocus()

    const openEvent = fireEvent.keyDown(trigger, { key: 'Enter', code: 'Enter' })
    expect(openEvent).toBe(true)
    fireEvent.click(trigger)

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toBeInTheDocument()

    const signOutButton = await screen.findByRole('button', { name: 'Sign out' })
    signOutButton.focus()
    expect(signOutButton).toHaveFocus()

    const activateEvent = fireEvent.keyDown(signOutButton, { key: ' ', code: 'Space' })
    expect(activateEvent).toBe(true)
    fireEvent.click(signOutButton)

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

describe('AccountMenu — D-217 feedback entry point', () => {
  it('offers a feedback row that swaps the sheet for the feedback dialog_D217', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Send feedback' }))

    // The sheet closes first, so exactly one dialog is ever mounted and focus is never
    // trapped in the panel behind the one the user is typing into.
    expect(await screen.findByRole('heading', { name: 'Send feedback' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(1))
    expect(screen.getByLabelText('Your message')).toBeInTheDocument()
  })

  it('returns focus to the account trigger when the feedback dialog closes_D217', async () => {
    mockProfile()
    renderMenu()

    const trigger = await screen.findByRole('button', { name: 'Account menu' })
    fireEvent.click(trigger)
    fireEvent.click(await screen.findByRole('button', { name: 'Send feedback' }))
    await screen.findByRole('heading', { name: 'Send feedback' })

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(trigger).toHaveFocus())
  })
})

describe('AccountMenu — D-220 install entry point', () => {
  const IPHONE_UA =
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'
  const originalUserAgent = navigator.userAgent

  function mockUserAgent(userAgent: string) {
    Object.defineProperty(navigator, 'userAgent', { configurable: true, value: userAgent })
  }

  afterEach(() => mockUserAgent(originalUserAgent))

  it('offers no install row on a browser with no install path_D220', async () => {
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    await screen.findByRole('button', { name: 'Send feedback' })
    expect(screen.queryByRole('button', { name: 'Install app' })).not.toBeInTheDocument()
  })

  it('offers an install row on iOS Safari that swaps the sheet for the instructions_D220', async () => {
    mockUserAgent(IPHONE_UA)
    mockProfile()
    renderMenu()

    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Install app' }))

    expect(
      await screen.findByRole('heading', { name: 'Add PartyUp to your Home Screen' }),
    ).toBeInTheDocument()
    await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(1))
  })

  it('returns focus to the account trigger when the install dialog closes_D220', async () => {
    mockUserAgent(IPHONE_UA)
    mockProfile()
    renderMenu()

    const trigger = await screen.findByRole('button', { name: 'Account menu' })
    fireEvent.click(trigger)
    fireEvent.click(await screen.findByRole('button', { name: 'Install app' }))
    await screen.findByRole('heading', { name: 'Add PartyUp to your Home Screen' })

    fireEvent.click(screen.getByRole('button', { name: 'Got it' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(trigger).toHaveFocus())
  })
})
