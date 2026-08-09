import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
}))

import '../../i18n'
import { PushEnableCard } from './PushEnableCard'

const ASKED_KEY = 'partyup:push-asked'
const SUBSCRIPTION_ID_KEY = 'partyup:push-subscription-id'

function renderCard() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <PushEnableCard />
    </QueryClientProvider>,
  )
}

function mockProfile(pushPromptEligible: boolean) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me') return Promise.resolve({ data: { pushPromptEligible } })
    if (path === '/api/v1/push/config')
      return Promise.resolve({ data: { vapidPublicKey: 'server-key', pushEnabled: true } })
    throw new Error(`Unexpected GET ${path}`)
  })
}

/** A minimal, real-enough PushManager/ServiceWorker stand-in — jsdom ships neither. */
function installPushSupport() {
  const subscription = {
    endpoint: 'https://push.example/abc',
    toJSON: () => ({
      endpoint: 'https://push.example/abc',
      keys: { p256dh: 'public-key', auth: 'auth-secret' },
    }),
  }
  const registration = {
    pushManager: { subscribe: vi.fn().mockResolvedValue(subscription) },
  }
  Object.defineProperty(window, 'PushManager', { value: function () {}, configurable: true })
  Object.defineProperty(navigator, 'serviceWorker', {
    value: {
      register: vi.fn().mockResolvedValue(registration),
      ready: Promise.resolve(registration),
    },
    configurable: true,
  })
}

function removePushSupport() {
  Reflect.deleteProperty(window, 'PushManager')
  Object.defineProperty(navigator, 'serviceWorker', { value: undefined, configurable: true })
}

beforeEach(() => {
  localStorage.clear()
  api.post.mockResolvedValue({ data: { id: 'subscription-1' } })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
  Reflect.deleteProperty(window, 'Notification')
})

describe('PushEnableCard — card visibility matrix (D-147/D-159, ruling 4)', () => {
  it('renders nothing when the account is not push-prompt-eligible', async () => {
    mockProfile(false)
    installPushSupport()
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission: vi.fn() },
      configurable: true,
    })

    renderCard()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument()
  })

  it('renders the card when eligible and this browser has not been asked', async () => {
    mockProfile(true)
    installPushSupport()
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission: vi.fn() },
      configurable: true,
    })

    renderCard()

    expect(await screen.findByTestId('push-enable-card')).toBeInTheDocument()
    // No sw registration on plain mount — only an explicit "Enable" click registers it.
    expect(navigator.serviceWorker.register).not.toHaveBeenCalled()
  })

  it('renders nothing when this browser was already asked (dismissed), even though eligible', async () => {
    localStorage.setItem(ASKED_KEY, '1')
    mockProfile(true)
    installPushSupport()
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission: vi.fn() },
      configurable: true,
    })

    renderCard()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument()
  })

  it('clicking "Not now" dismisses the card and never re-nags on remount', async () => {
    mockProfile(true)
    installPushSupport()
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission: vi.fn() },
      configurable: true,
    })

    const { unmount } = renderCard()
    fireEvent.click(await screen.findByRole('button', { name: 'Not now' }))

    await waitFor(() =>
      expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument(),
    )
    expect(localStorage.getItem(ASKED_KEY)).toBe('1')

    unmount()
    renderCard()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument()
  })

  it('an explicit grant registers the worker, subscribes and POSTs — then never re-nags', async () => {
    mockProfile(true)
    installPushSupport()
    const requestPermission = vi.fn().mockResolvedValue('granted')
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission },
      configurable: true,
    })

    renderCard()
    fireEvent.click(await screen.findByRole('button', { name: 'Enable notifications' }))

    await waitFor(() => expect(requestPermission).toHaveBeenCalled())
    await waitFor(() => expect(navigator.serviceWorker.register).toHaveBeenCalledWith('/sw.js'))
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/push/subscriptions',
        expect.objectContaining({
          body: {
            channel: 'webpush',
            endpoint: 'https://push.example/abc',
            publicKey: 'public-key',
            authSecret: 'auth-secret',
          },
        }),
      ),
    )
    await waitFor(() =>
      expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument(),
    )
    expect(localStorage.getItem(SUBSCRIPTION_ID_KEY)).toBe('subscription-1')
  })

  it('a denied permission shows the D-175 fallback notice instead of the card_D175', async () => {
    mockProfile(true)
    installPushSupport()
    const requestPermission = vi.fn().mockResolvedValue('denied')
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission },
      configurable: true,
    })

    renderCard()
    fireEvent.click(await screen.findByRole('button', { name: 'Enable notifications' }))

    expect(
      await screen.findByText(
        "This browser can't show push notifications. You'll still see updates in your notification center.",
      ),
    ).toBeInTheDocument()
    expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
    // The user's own click prompted exactly once — the fallback notice does not itself re-prompt.
    expect(requestPermission).toHaveBeenCalledTimes(1)
  })

  it('an absent PushManager shows the D-175 fallback notice without ever prompting_D175', async () => {
    mockProfile(true)
    removePushSupport()
    const requestPermission = vi.fn().mockResolvedValue('granted')
    Object.defineProperty(window, 'Notification', {
      value: { permission: 'default', requestPermission },
      configurable: true,
    })

    renderCard()

    expect(
      await screen.findByText(
        "This browser can't show push notifications. You'll still see updates in your notification center.",
      ),
    ).toBeInTheDocument()
    expect(screen.queryByTestId('push-enable-card')).not.toBeInTheDocument()
    expect(localStorage.getItem(ASKED_KEY)).toBe('1')
    expect(requestPermission).not.toHaveBeenCalled()
  })
})
