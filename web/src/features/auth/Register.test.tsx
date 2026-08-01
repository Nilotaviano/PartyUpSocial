import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
vi.mock('../../api/client', () => ({ client: { POST: vi.fn().mockResolvedValue({}) }, ApiError: class extends Error {}, isApiError: () => false, refreshAntiforgeryToken: vi.fn() }))
import '../../i18n'
import { Register } from './AuthScreens'
function Location() { return <output data-testid="location">{useLocation().pathname}</output> }
describe('Register', () => {
  it('shows check-your-inbox without navigating to onboarding_D016', async () => {
    sessionStorage.setItem('partyup.age-gate-attested', 'true')
    render(<MemoryRouter initialEntries={['/register']}><Register /><Location /></MemoryRouter>)
    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'user@example.test' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Create account' }).closest('form')!)
    expect(await screen.findByText('Check your inbox.')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/register')
  })
})