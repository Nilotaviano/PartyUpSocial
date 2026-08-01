import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => {
  class ApiError extends Error {
    code: string
    constructor(code: string) { super(code); this.code = code }
  }
  return { get: vi.fn(), remove: vi.fn(), ApiError }
})

vi.mock('../../api/client', () => ({
  client: { GET: api.get, DELETE: api.remove },
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))
vi.mock('../../auth/useSession', () => ({ useSession: vi.fn().mockReturnValue({ data: { profile: null } }), sessionQueryKey: ['session'] }))

import '../../i18n'
import { AccountSettingsPage } from './AccountSettingsPage'

describe('AuthMethods', () => {
  it('shows the explicit API error when unlinking the last method_D137', async () => {
    api.get.mockResolvedValue({ data: { methods: [
      { kind: 'password', linked: true, removable: false, detail: 'Password' },
      { kind: 'google', linked: true, removable: true, detail: 'Google' },
    ] } })
    api.remove.mockRejectedValue(new api.ApiError('last_auth_method'))

    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>)

    fireEvent.click(await screen.findByRole('button', { name: 'Unlink google' }))
    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'correct-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirm with password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('You need at least one sign-in method.')
  })
})