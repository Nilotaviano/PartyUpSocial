import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  isApiError: () => false,
}))

import '../../i18n'
import { PublicDeletionConfirmPage } from './PublicDeletionConfirmPage'
import { PublicDeletionRequestPage } from './PublicDeletionRequestPage'

describe('PublicDeletionPage', () => {
  beforeEach(() => {
    api.post.mockReset()
    api.post.mockResolvedValue({})
    window.history.replaceState({}, '', '/account-deletion')
  })

  it('offers both the email and Steam proof routes_D072', () => {
    render(<PublicDeletionRequestPage />)

    expect(screen.getByLabelText('Email address')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send deletion instructions' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Prove it with Steam' })).toBeInTheDocument()
  })

  it('clears a hash token before render, never displays it, and only posts after confirmation_D072', async () => {
    const token = 'raw-deletion-token'
    window.history.replaceState({}, '', `/account-deletion/confirm#token=${token}`)

    const { container } = render(<PublicDeletionConfirmPage />)

    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain(token)
    expect(container.textContent).not.toContain(token)
    expect(api.post).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm account deletion' }))

    expect(api.post).toHaveBeenCalledWith('/api/v1/account-deletion-requests/confirm', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { token },
    })
  })
})