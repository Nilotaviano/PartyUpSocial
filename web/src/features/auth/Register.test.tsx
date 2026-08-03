import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  ApiError: class extends Error {},
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { Register } from './AuthScreens'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function renderRegister(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Register />
      <Location />
    </MemoryRouter>,
  )
}

function submitRegistration() {
  fireEvent.change(screen.getByLabelText('Email address'), {
    target: { value: 'user@example.test' },
  })
  fireEvent.submit(
    screen.getByRole('button', { name: 'Create account' }).closest('form')!,
  )
}

afterEach(() => {
  cleanup()
  sessionStorage.clear()
  vi.clearAllMocks()
})

describe('Register', () => {
  it.each([
    ['/register?next=%2Fu%2FFriend.One', '/u/friend.one'],
    ['/register?next=%2Fadd%2F2345-6789-ab', '/add/23456789AB'],
  ])(
    'sends a canonical friend continuation and stays on the inbox screen_D031',
    async (path, continuation) => {
      sessionStorage.setItem('partyup.age-gate-attested', 'true')
      api.post.mockResolvedValue({})
      renderRegister(path)

      submitRegistration()

      expect(await screen.findByText('Check your inbox.')).toBeInTheDocument()
      expect(api.post).toHaveBeenCalledWith('/api/v1/auth/register', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email: 'user@example.test',
          ageGateAttested: true,
          locale: 'en',
          continuation,
        },
      })
      expect(screen.getByTestId('location')).toHaveTextContent('/register')
    },
  )

  it('drops a hostile continuation before issuing registration_D031', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    api.post.mockResolvedValue({})
    renderRegister('/register?next=https%3A%2F%2Fevil.test%2Fu%2Fplayer')

    submitRegistration()

    await screen.findByText('Check your inbox.')
    expect(api.post.mock.calls[0][1].body.continuation).toBeNull()
  })

  it('carries a canonical continuation through the age gate_D031', () => {
    renderRegister('/register?next=%2Fu%2FFriend.One')

    submitRegistration()

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/age-gate?next=%2Fregister%3Fnext%3D%252Fu%252Ffriend.one',
    )
    expect(api.post).not.toHaveBeenCalled()
  })
})
