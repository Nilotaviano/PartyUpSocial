import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { POST: api.post },
  isApiError: () => false,
}))

import '../../i18n'
import { EmailUnsubscribePage } from './EmailUnsubscribePage'

describe('EmailUnsubscribePage (D189_DESIGN.md §3)', () => {
  beforeEach(() => {
    api.post.mockReset()
    api.post.mockResolvedValue({})
    window.history.replaceState({}, '', '/email-unsubscribe')
  })

  afterEach(() => {
    cleanup()
  })

  it('is inert on mount: clears a hash token before render, never displays it, and does not POST', async () => {
    const token = 'raw-unsubscribe-token'
    window.history.replaceState({}, '', `/email-unsubscribe#token=${token}`)

    const { container } = render(<EmailUnsubscribePage />)

    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain(token)
    expect(container.textContent).not.toContain(token)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('fires the POST with the hash token only after the button click, then shows confirmation', async () => {
    const token = 'raw-unsubscribe-token'
    window.history.replaceState({}, '', `/email-unsubscribe#token=${token}`)

    render(<EmailUnsubscribePage />)

    fireEvent.click(screen.getByRole('button', { name: 'Unsubscribe' }))

    expect(api.post).toHaveBeenCalledWith('/api/v1/email-notifications/unsubscribe', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { token },
    })
    expect(
      await screen.findByText('You have been unsubscribed from email notifications.'),
    ).toBeInTheDocument()
  })

  it('disables the Unsubscribe button while the request is in flight, preventing a double-post (Wave 6)', () => {
    const token = 'raw-unsubscribe-token'
    window.history.replaceState({}, '', `/email-unsubscribe#token=${token}`)
    api.post.mockImplementation(() => new Promise(() => {}))

    render(<EmailUnsubscribePage />)
    const button = screen.getByRole('button', { name: 'Unsubscribe' })
    fireEvent.click(button)

    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('aria-busy', 'true')
    expect(api.post).toHaveBeenCalledTimes(1)
  })

  it('shows an invalid-link message and no button when there is no token', () => {
    render(<EmailUnsubscribePage />)

    expect(screen.getByText('This unsubscribe link is invalid or expired.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Unsubscribe' })).not.toBeInTheDocument()
  })
})
