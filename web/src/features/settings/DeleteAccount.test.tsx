import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

vi.mock('../../api/client', () => ({ client: { GET: vi.fn().mockResolvedValue({ data: { methods: [] } }) }, isApiError: () => false }))
vi.mock('../../auth/useSession', () => ({ useSession: vi.fn().mockReturnValue({ data: { profile: null } }), sessionQueryKey: ['session'] }))

import '../../i18n'
import { AccountSettingsPage } from './AccountSettingsPage'

function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>)
}

describe('DeleteAccount', () => {
  it('states the seven-day grace and login-cancels rule before confirming_D148_D158', () => {
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: 'Delete my account' }))

    expect(screen.getByRole('dialog')).toHaveTextContent('Deletion starts a 7-day grace period before destructive effects occur.')
    expect(screen.getByRole('dialog')).toHaveTextContent('Signing in during that grace period cancels the deletion request.')
  })
})