import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Outlet, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const auth = vi.hoisted(() => ({ session: undefined as unknown }))

vi.mock('../../auth/useSession', () => ({
  useSession: () => ({ data: auth.session, isPending: false }),
}))

import { RequireAuth } from '../../auth/RequireAuth'
import { RequireOnboarded } from '../../auth/RequireOnboarded'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

function GuardedRoutes() {
  return (
    <>
      <Routes>
        <Route path="/login" element={<div data-testid="login" />} />
        <Route path="/onboarding" element={<div data-testid="onboarding" />} />
        <Route element={<RequireAuth />}>
          <Route element={<RequireOnboarded />}>
            <Route
              path="/u/:username"
              element={
                <div data-testid="profile">
                  <Outlet />
                </div>
              }
            />
            <Route path="/add/:code" element={<div data-testid="add" />} />
          </Route>
        </Route>
      </Routes>
      <Location />
    </>
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('friend route guards', () => {
  it('canonicalizes a profile continuation through anonymous authentication_D031', () => {
    auth.session = {
      authenticated: false,
      state: null,
      onboarding: null,
    }
    render(
      <MemoryRouter initialEntries={['/u/Friend.One']}>
        <GuardedRoutes />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('login')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/login?next=%2Fu%2Ffriend.one',
    )
  })

  it('retains an add continuation through incomplete-account onboarding_D031', () => {
    auth.session = {
      authenticated: true,
      state: 'onboarding',
      onboarding: { completedSteps: [], nextStep: 'username' },
    }
    render(
      <MemoryRouter initialEntries={['/add/2345-6789-ab']}>
        <GuardedRoutes />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('onboarding')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/onboarding?next=%2Fadd%2F23456789AB',
    )
  })

  it('drops hostile encoded and extra-segment continuations_D031', () => {
    auth.session = {
      authenticated: false,
      state: null,
      onboarding: null,
    }
    render(
      <MemoryRouter initialEntries={['/u/player%252Fextra']}>
        <GuardedRoutes />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/login?next=%2Fapp',
    )
  })
})
