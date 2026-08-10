import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  ApiError: class extends Error {},
  isApiError: () => false,
  refreshAntiforgeryToken: vi.fn(),
}))

import '../../i18n'
import { AgeGate, Register } from './AuthScreens'

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
    ['', 'Enter your email address.'],
    ['not-an-email', 'Enter a valid email address.'],
  ])(
    'rejects %s app-side with a localized error instead of the browser bubble, without POSTing_D184',
    async (value, message) => {
      sessionStorage.setItem('partyup.age-gate-attested', 'true')
      renderRegister('/register')

      if (value) {
        fireEvent.change(screen.getByLabelText('Email address'), {
          target: { value },
        })
      }
      fireEvent.submit(
        screen.getByRole('button', { name: 'Create account' }).closest('form')!,
      )

      expect(await screen.findByRole('alert')).toHaveTextContent(message)
      expect(api.post).not.toHaveBeenCalled()
      expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true')
    },
  )

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

  it('preserves the typed email through an age-gate round trip and auto-resumes registration_D083', async () => {
    api.post.mockResolvedValue({})
    render(
      <MemoryRouter initialEntries={['/register']}>
        <Routes>
          <Route path="/register" element={<Register />} />
          <Route path="/age-gate" element={<AgeGate />} />
        </Routes>
        <Location />
      </MemoryRouter>,
    )

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'keepme@example.test' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Create account' }).closest('form')!,
    )

    await screen.findByTestId('screen-age-gate')
    expect(api.post).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByLabelText('Email address')).toHaveValue(
      'keepme@example.test',
    )

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/auth/register', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email: 'keepme@example.test',
          ageGateAttested: true,
          locale: 'en',
          continuation: null,
        },
      }),
    )
  })

  it('fires exactly one POST for a failed resume and none on a later remount_D083', async () => {
    api.post.mockRejectedValue(new Error('boom'))
    const { unmount } = render(
      <MemoryRouter initialEntries={['/register']}>
        <Routes>
          <Route path="/register" element={<Register />} />
          <Route path="/age-gate" element={<AgeGate />} />
        </Routes>
        <Location />
      </MemoryRouter>,
    )

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'flaky@example.test' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Create account' }).closest('form')!,
    )

    await screen.findByTestId('screen-age-gate')
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    unmount()

    render(
      <MemoryRouter initialEntries={['/register']}>
        <Register />
        <Location />
      </MemoryRouter>,
    )

    expect(screen.getByLabelText('Email address')).toHaveValue('')
    expect(api.post).toHaveBeenCalledTimes(1)
  })

  it('prefers the current location continuation over a stashed one on resume_D031', async () => {
    api.post.mockResolvedValue({})
    const { unmount } = renderRegister('/register?next=%2Fu%2Fstale.user')

    fireEvent.change(screen.getByLabelText('Email address'), {
      target: { value: 'newlink@example.test' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Create account' }).closest('form')!,
    )

    expect(api.post).not.toHaveBeenCalled()
    unmount()

    render(
      <MemoryRouter
        initialEntries={[
          {
            pathname: '/register',
            search: '?next=%2Fu%2Ffresh.user',
            state: { justAttested: true },
          },
        ]}
      >
        <Register />
        <Location />
      </MemoryRouter>,
    )

    expect(screen.getByLabelText('Email address')).toHaveValue(
      'newlink@example.test',
    )
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/auth/register', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email: 'newlink@example.test',
          ageGateAttested: true,
          locale: 'en',
          continuation: '/u/fresh.user',
        },
      }),
    )
  })
})
