import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  refresh: vi.fn(),
}))

const auth = vi.hoisted(() => ({
  session: {
    authenticated: true,
    userId: 'user-1',
    providers: [],
    state: 'onboarding',
    isAdmin: false,
    onboarding: { completedSteps: ['ageGate', 'auth'], nextStep: 'username' },
    pendingDeletion: null,
    profile: null,
  } as Record<string, unknown>,
}))

vi.mock('../../api/client', () => {
  class ApiError extends Error {
    readonly status: number
    readonly code: string

    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  }

  return {
    ApiError,
    client: { GET: api.get, POST: api.post },
    isApiError: (error: unknown) => error instanceof ApiError,
    refreshAntiforgeryToken: api.refresh,
  }
})

vi.mock('../../auth/useSession', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../auth/useSession')>()
  return {
    ...actual,
    useSession: () => ({ data: auth.session, isPending: false }),
  }
})

import { client } from '../../api/client'
import '../../i18n'
import { OnboardingWizard } from './OnboardingWizard'

const storageKey = 'partyup.onboarding-draft'
const completeDraft = {
  username: 'partyup',
  displayName: 'Party Up',
  avatar: { kind: 'emoji', emoji: 'game', platformLinkId: null },
  timeZone: 'UTC',
  locale: null,
}

function LocationIndicator() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function renderWizard(path: string) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/onboarding/*" element={<OnboardingWizard />} />
          <Route path="/app" element={<div data-testid="screen-app" />} />
        </Routes>
        <LocationIndicator />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function expectHeading(name: string) {
  await waitFor(() =>
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(name),
  )
}

function activeSession(continuation: string | null = null) {
  return {
    authenticated: true,
    userId: 'user-1',
    providers: [],
    state: 'active',
    isAdmin: false,
    onboarding: null,
    pendingDeletion: null,
    profile: {
      username: 'partyup',
      displayName: 'Party Up',
      avatar: { kind: 'emoji', emoji: 'game', sourcePlatformLinkId: null },
      timeZone: 'UTC',
      locale: 'en',
      friendCode: '23456789AB',
    },
    continuation,
  }
}

beforeEach(() => {
  sessionStorage.clear()
  auth.session = {
    authenticated: true,
    userId: 'user-1',
    providers: [],
    state: 'onboarding',
    isAdmin: false,
    onboarding: { completedSteps: ['ageGate', 'auth'], nextStep: 'username' },
    pendingDeletion: null,
    profile: null,
  }
  api.refresh.mockResolvedValue('csrf-token')
  api.post.mockResolvedValue({ data: {} })
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/onboarding/username/availability') {
      return Promise.resolve({ data: { available: true, reason: null } })
    }
    if (path === '/api/v1/auth/session') {
      return Promise.resolve({ data: activeSession() })
    }
    if (path === '/api/v1/friend-share') {
      return Promise.resolve({
        data: {
          friendCode: '23456789AB',
          addUrl: 'https://partyup.test/add/23456789AB',
          profileUrl: 'https://partyup.test/u/partyup',
        },
      })
    }
    if (
      path === '/api/v1/users/by-username/{username}' ||
      path === '/api/v1/friend-codes/{friendCode}'
    ) {
      return Promise.resolve({
        data: {
          profile: {
            disclosure: 'username_only',
            username: 'friend.one',
            relationship: { kind: 'none' },
          },
        },
      })
    }
    throw new Error('Unexpected GET ' + path)
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('OnboardingWizard', () => {
  it('advances in D-127 order and activates before rendering friend tools_D127', async () => {
    renderWizard('/onboarding?next=%2Fu%2FFriend.One')

    await expectHeading('Choose a username')
    fireEvent.change(screen.getByLabelText('Username'), {
      target: { value: 'partyup' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )
    await expectHeading('Choose a display name')

    fireEvent.change(screen.getByLabelText('Display name'), {
      target: { value: 'Party Up' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )
    await expectHeading('Choose an avatar')

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Choose your time zone')

    fireEvent.submit(
      screen.getByRole('button', { name: 'Continue' }).closest('form')!,
    )
    await expectHeading('Link platforms')
    expect(screen.getByTestId('location')).toHaveTextContent(
      'next=%2Fu%2Ffriend.one',
    )

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Find friends')
    await screen.findByText('@friend.one')

    const completeOrder = api.post.mock.invocationCallOrder[0]
    const refreshOrder = api.refresh.mock.invocationCallOrder[0]
    const sessionOrder = api.get.mock.invocationCallOrder.find(
      (_, index) => api.get.mock.calls[index][0] === '/api/v1/auth/session',
    )!
    const firstFriendOrder = api.get.mock.invocationCallOrder.find((_, index) =>
      String(api.get.mock.calls[index][0]).startsWith('/api/v1/friend'),
    )!

    expect(completeOrder).toBeLessThan(refreshOrder)
    expect(refreshOrder).toBeLessThan(sessionOrder)
    expect(sessionOrder).toBeLessThan(firstFriendOrder)
    expect(screen.getByRole('heading', { level: 1 })).toHaveFocus()
  })

  it('redirects an incomplete deep link to the first incomplete step while retaining next_D127', async () => {
    sessionStorage.setItem(storageKey, JSON.stringify({ username: 'partyup' }))
    renderWizard('/onboarding/friends?next=%2Fadd%2F23456789AB')

    await expectHeading('Choose a display name')
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/onboarding/display-name?next=%2Fadd%2F23456789AB',
    )
    expect(client.POST).not.toHaveBeenCalled()
  })

  it('posts the complete draft on leaving platform linking and never on Finish_D127', async () => {
    sessionStorage.setItem(storageKey, JSON.stringify(completeDraft))
    renderWizard('/onboarding/link-platforms')

    await expectHeading('Link platforms')
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Find friends')
    await screen.findByRole('button', { name: 'Finish setup' })

    expect(client.POST).toHaveBeenCalledTimes(1)
    expect(client.POST).toHaveBeenCalledWith('/api/v1/onboarding/complete', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { ...completeDraft, locale: 'en' },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Finish setup' }))
    expect(client.POST).toHaveBeenCalledTimes(1)
    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent('/app'),
    )
  })

  it('keeps a refresh failure retryable and makes no friend call before retry_D127', async () => {
    sessionStorage.setItem(storageKey, JSON.stringify(completeDraft))
    api.refresh.mockRejectedValueOnce(new Error('refresh failed'))
    renderWizard('/onboarding/link-platforms?next=%2Fadd%2F23456789AB')

    fireEvent.click(await screen.findByRole('button', { name: 'Continue' }))
    expect(
      await screen.findByRole('heading', {
        name: 'Your profile is active, but PartyUp could not refresh your secure session.',
      }),
    ).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/onboarding/friends?next=%2Fadd%2F23456789AB',
    )
    expect(
      api.get.mock.calls.some(([path]) =>
        String(path).startsWith('/api/v1/friend'),
      ),
    ).toBe(false)

    api.refresh.mockResolvedValue('new-token')
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    await screen.findByText('@friend.one')
    expect(client.POST).toHaveBeenCalledTimes(1)
    expect(
      api.get.mock.calls.some(
        ([path]) => path === '/api/v1/friend-codes/{friendCode}',
      ),
    ).toBe(true)
  })

  it('renders the optional friends step on an Active direct reload without completing again_D127', async () => {
    auth.session = activeSession('/u/friend.one')
    renderWizard('/onboarding/friends?next=%2Fu%2Ffriend.one')

    await expectHeading('Find friends')
    await screen.findByText('@friend.one')
    expect(client.POST).not.toHaveBeenCalled()
    expect(
      screen.getByRole('button', { name: 'Skip for now' }),
    ).toBeInTheDocument()
  })
})
