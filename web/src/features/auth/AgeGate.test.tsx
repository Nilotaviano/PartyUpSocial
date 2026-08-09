import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import '../../i18n'
import { AgeGate } from './AuthScreens'

function Location() {
  const location = useLocation()
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  )
}

afterEach(() => {
  cleanup()
  sessionStorage.clear()
})

describe('AgeGate', () => {
  it('blocks continue until the checkbox is ticked_D083', () => {
    render(
      <MemoryRouter initialEntries={['/age-gate']}>
        <AgeGate />
        <Location />
      </MemoryRouter>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/age-gate')

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(sessionStorage.getItem('partyup.age-gate-attested')).toBe('true')
    expect(screen.getByTestId('location')).toHaveTextContent('/register')
  })

  it('returns to registration with only a validated friend continuation_D031', () => {
    const returnTo = encodeURIComponent('/register?next=%2Fadd%2F2345-6789-ab')
    render(
      <MemoryRouter initialEntries={['/age-gate?next=' + returnTo]}>
        <AgeGate />
        <Location />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/register?next=%2Fadd%2F23456789AB',
    )
  })

  it('returns to login with only a validated friend continuation_D083', () => {
    const returnTo = encodeURIComponent('/login?next=%2Fadd%2F2345-6789-ab')
    render(
      <MemoryRouter initialEntries={['/age-gate?next=' + returnTo]}>
        <AgeGate />
        <Location />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/login?next=%2Fadd%2F23456789AB',
    )
  })

  it('drops an external return target_D031', () => {
    render(
      <MemoryRouter initialEntries={['/age-gate?next=https%3A%2F%2Fevil.test']}>
        <AgeGate />
        <Location />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByTestId('location')).toHaveTextContent('/register')
  })
})
