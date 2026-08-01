import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

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
    client: { GET: vi.fn(), POST: vi.fn() },
    isApiError: (error: unknown) => error instanceof ApiError,
    refreshAntiforgeryToken: vi.fn(),
  }
})

import { client } from '../../api/client'
import '../../i18n'
import { OnboardingWizard } from './OnboardingWizard'

const storageKey = 'partyup.onboarding-draft'

function LocationIndicator() {
  return <output data-testid="location">{useLocation().pathname}</output>
}

function renderWizard(path: string) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[path]}>
        <OnboardingWizard />
        <LocationIndicator />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function expectHeading(name: string) {
  await waitFor(() => expect(screen.getByRole('heading')).toHaveTextContent(name))
}

beforeEach(() => {
  sessionStorage.clear()
  vi.mocked(client.GET).mockResolvedValue({ data: { available: true, reason: null } } as never)
  vi.mocked(client.POST).mockResolvedValue({ data: {} } as never)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('OnboardingWizard', () => {
  it('advances through the D-127 steps in order_D127', async () => {
    renderWizard('/onboarding')

    await expectHeading('Choose a username')
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'partyup' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)
    await expectHeading('Choose a display name')

    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Party Up' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)
    await expectHeading('Choose an avatar')

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Choose your time zone')

    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)
    await expectHeading('Link platforms')

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Find friends')
  })

  it('redirects a past deep link to the first incomplete step_D127', async () => {
    sessionStorage.setItem(storageKey, JSON.stringify({ username: 'partyup' }))
    renderWizard('/onboarding/friends')

    await expectHeading('Choose a display name')
    expect(screen.getByTestId('location')).toHaveTextContent('/onboarding/display-name')
  })

  it('restores its draft after unmounting and remounting_D127', async () => {
    const view = renderWizard('/onboarding')
    await expectHeading('Choose a username')
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'partyup' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)
    await expectHeading('Choose a display name')
    expect(JSON.parse(sessionStorage.getItem(storageKey)!)).toMatchObject({ username: 'partyup' })

    view.unmount()
    renderWizard('/onboarding/display-name')
    await expectHeading('Choose a display name')
  })

  it('posts the complete draft exactly once_D127', async () => {
    const draft = {
      username: 'partyup',
      displayName: 'Party Up',
      avatar: { kind: 'emoji', emoji: 'game', platformLinkId: null },
      timeZone: 'UTC',
      locale: null,
    }
    sessionStorage.setItem(storageKey, JSON.stringify(draft))
    renderWizard('/onboarding/link-platforms')

    await expectHeading('Link platforms')
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Find friends')
    fireEvent.click(screen.getByRole('button', { name: 'Finish setup' }))

    await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1))
    expect(client.POST).toHaveBeenCalledWith('/api/v1/onboarding/complete', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { ...draft, locale: 'en' },
    })
  })
})
