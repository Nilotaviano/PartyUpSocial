import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { describe, expect, it } from 'vitest'
import '../../i18n'
import { AgeGate } from './AuthScreens'
function Location() { return <output data-testid="location">{useLocation().pathname}</output> }
describe('AgeGate', () => {
  it('blocks continue until the checkbox is ticked_D083', () => {
    sessionStorage.clear()
    render(<MemoryRouter initialEntries={['/age-gate']}><AgeGate /><Location /></MemoryRouter>)
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/age-gate')
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(sessionStorage.getItem('partyup.age-gate-attested')).toBe('true')
    expect(screen.getByTestId('location')).toHaveTextContent('/register')
  })
})