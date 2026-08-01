import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
vi.mock('../../api/client', () => ({ client: { GET: vi.fn() }, isApiError: () => true }))
import { client } from '../../api/client'
import '../../i18n'
import { UsernameStep } from './OnboardingWizard'
afterEach(cleanup)
describe('UsernameStep', () => {
  it.each([
    ['username.taken', 'That username is unavailable.'],
    ['username.reserved', 'That username is reserved.'],
    ['username.cooldown', 'That username cannot be changed yet.'],
  ])('renders errors.%s from the API code_D128_D145', async (code, expected) => {
    vi.mocked(client.GET).mockRejectedValueOnce({ code })
    render(<UsernameStep value={null} onNext={vi.fn()} />)
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'partyup' } })
    fireEvent.submit(screen.getByRole('button', { name: 'Continue' }).closest('form')!)
    expect(await screen.findByRole('alert')).toHaveTextContent(expected)
  })
})