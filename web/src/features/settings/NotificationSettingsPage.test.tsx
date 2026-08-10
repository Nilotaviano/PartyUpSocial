import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

import '../../i18n'
import { NotificationSettingsPage } from './NotificationSettingsPage'

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <NotificationSettingsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function mockProfile(reminderDefault = 'Off') {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me')
      return Promise.resolve({
        data: { reminderDefault, pushPromptEligible: true },
      })
    if (path === '/api/v1/push/config')
      return Promise.resolve({ data: { vapidPublicKey: 'server-key', pushEnabled: true } })
    throw new Error(`Unexpected GET ${path}`)
  })
}

function installPushSupport() {
  const subscription = {
    endpoint: 'https://push.example/abc',
    toJSON: () => ({
      endpoint: 'https://push.example/abc',
      keys: { p256dh: 'public-key', auth: 'auth-secret' },
    }),
    unsubscribe: vi.fn().mockResolvedValue(true),
  }
  const registration = {
    pushManager: {
      subscribe: vi.fn().mockResolvedValue(subscription),
      getSubscription: vi.fn().mockResolvedValue(subscription),
    },
  }
  Object.defineProperty(window, 'PushManager', { value: function () {}, configurable: true })
  Object.defineProperty(navigator, 'serviceWorker', {
    value: {
      register: vi.fn().mockResolvedValue(registration),
      ready: Promise.resolve(registration),
      getRegistration: vi.fn().mockResolvedValue(registration),
    },
    configurable: true,
  })
  Object.defineProperty(window, 'Notification', {
    value: { permission: 'default', requestPermission: vi.fn().mockResolvedValue('granted') },
    configurable: true,
  })
}

function removePushSupport() {
  Reflect.deleteProperty(window, 'PushManager')
  Object.defineProperty(navigator, 'serviceWorker', { value: undefined, configurable: true })
}

beforeEach(() => {
  localStorage.clear()
  api.put.mockResolvedValue({ data: {} })
  api.post.mockResolvedValue({ data: { id: 'subscription-1' } })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
  Reflect.deleteProperty(window, 'Notification')
})

describe('NotificationSettingsPage — the three ruling-3 controls', () => {
  it('shows the unsupported message instead of a toggle when PushManager is absent', async () => {
    mockProfile()
    removePushSupport()

    renderPage()

    expect(
      await screen.findByText('This browser does not support push notifications.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Turn on for this browser' })).not.toBeInTheDocument()
  })

  it('turning push on requests permission, subscribes and POSTs — then reads as on', async () => {
    mockProfile()
    installPushSupport()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Turn on for this browser' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/push/subscriptions',
        expect.objectContaining({
          body: expect.objectContaining({ channel: 'webpush' }),
        }),
      ),
    )
    expect(
      await screen.findByRole('button', { name: 'Turn off for this browser' }),
    ).toBeInTheDocument()
  })

  it('turning push off unsubscribes and DELETEs the stored subscription id', async () => {
    localStorage.setItem('partyup:push-subscription-id', 'subscription-1')
    mockProfile()
    installPushSupport()
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'granted', requestPermission: vi.fn() },
      configurable: true,
    })

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Turn off for this browser' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/push/subscriptions/{subscriptionId}',
        expect.objectContaining({
          params: expect.objectContaining({ path: { subscriptionId: 'subscription-1' } }),
        }),
      ),
    )
    expect(localStorage.getItem('partyup:push-subscription-id')).toBeNull()
  })

  it('renders translated reminder option labels, never raw i18n keys_D082', async () => {
    mockProfile('Off')
    installPushSupport()

    renderPage()
    const select = (await screen.findByLabelText(
      'Remind me before a session starts',
    )) as HTMLSelectElement

    expect([...select.options].map((option) => option.textContent)).toEqual([
      'Off',
      '15 minutes before',
      '30 minutes before',
      '1 hour before',
      '3 hours before',
      '12 hours before',
      '24 hours before',
    ])
  })

  it.each([
    ['Off', 'Off'],
    ['Minutes15', 'Min15'],
    ['Minutes30', 'Min30'],
    ['Hours1', 'Hours1'],
    ['Hours3', 'Hours3'],
    ['Hours12', 'Hours12'],
    ['Hours24', 'Hours24'],
  ])(
    'changing the account default reminder to %s PUTs the wire value %s and confirms it saved_D082',
    async (optionValue, wireValue) => {
      mockProfile('Off')
      installPushSupport()

      renderPage()
      const select = await screen.findByLabelText('Remind me before a session starts')
      fireEvent.change(select, { target: { value: optionValue } })

      await waitFor(() =>
        expect(api.put).toHaveBeenCalledWith(
          '/api/v1/me/reminder-default',
          expect.objectContaining({ body: { reminderDefault: wireValue } }),
        ),
      )
      expect(await screen.findByText('Default reminder saved.')).toBeInTheDocument()
    },
  )

  it.each([
    ['Off', 'Off'],
    ['Min15', 'Minutes15'],
    ['Min30', 'Minutes30'],
    ['Hours1', 'Hours1'],
    ['Hours3', 'Hours3'],
    ['Hours12', 'Hours12'],
    ['Hours24', 'Hours24'],
  ])(
    'the server wire value %s selects the %s option on load_D082',
    async (wireValue, optionValue) => {
      mockProfile(wireValue)
      installPushSupport()

      renderPage()
      const select = (await screen.findByLabelText(
        'Remind me before a session starts',
      )) as HTMLSelectElement

      await waitFor(() => expect(select.value).toBe(optionValue))
    },
  )

  it('links out to per-session/poll muting rather than duplicating it here (ruling 3)', async () => {
    mockProfile()
    installPushSupport()

    renderPage()

    expect(await screen.findByRole('link', { name: 'Go to your sessions' })).toHaveAttribute(
      'href',
      '/sessions/upcoming',
    )
  })
})
