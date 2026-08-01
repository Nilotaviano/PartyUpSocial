import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

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
import { UsernameStep } from './OnboardingWizard'

afterEach(cleanup)

describe('UsernameStep', () => {
  it.each([
    ['taken', 'That username is unavailable.'],
    ['reserved', 'That username is reserved.'],
    ['cooldown', 'That username cannot be changed yet.'],
  ])('renders errors.username.%s from a 200 availability response_D128_D145', async (reason, expected) => {
    const onNext = vi.fn()
    vi.mocked(client.GET).mockResolvedValueOnce({ data: { available: false, reason } } as never)
    render(<UsernameStep value={null} onNext={onNext} />)

    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'partyup' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)

    expect(await screen.findByRole('alert')).toHaveTextContent(expected)
    expect(onNext).not.toHaveBeenCalled()
  })
})
