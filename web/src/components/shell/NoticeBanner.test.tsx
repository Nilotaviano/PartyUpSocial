import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Bell } from 'lucide-react'

import { NoticeBanner } from './NoticeBanner'

afterEach(cleanup)

describe('NoticeBanner — prompt variant', () => {
  it('renders icon, title, body and fires the action/dismiss callbacks', () => {
    const onAction = vi.fn()
    const onDismiss = vi.fn()

    render(
      <NoticeBanner
        actionLabel="Enable"
        body="Get pinged when a poll needs your vote."
        dismissLabel="Not now"
        icon={Bell}
        title="Push notifications are off."
        onAction={onAction}
        onDismiss={onDismiss}
      />,
    )

    expect(screen.getByText('Push notifications are off.')).toBeInTheDocument()
    expect(screen.getByText('Get pinged when a poll needs your vote.')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Enable' }))
    expect(onAction).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: 'Not now' }))
    expect(onDismiss).toHaveBeenCalledTimes(1)
  })

  it('marks the action button aria-busy and disabled while busy', () => {
    render(
      <NoticeBanner actionBusy actionLabel="Enable" title="Push notifications are off." onAction={vi.fn()} />,
    )

    const button = screen.getByRole('button', { name: 'Enable' })
    expect(button).toHaveAttribute('aria-busy', 'true')
    expect(button).toBeDisabled()
  })

  it('renders an error line with role="alert" when supplied', () => {
    render(<NoticeBanner error="Something went wrong." title="Push notifications are off." />)

    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong.')
  })

  it('carries a caller-supplied data-testid', () => {
    render(<NoticeBanner data-testid="push-enable-card" title="Push notifications are off." />)

    expect(screen.getByTestId('push-enable-card')).toBeInTheDocument()
  })
})

describe('NoticeBanner — status variant (D-175 fallback)', () => {
  it('renders a role="status" line with no icon or buttons', () => {
    render(<NoticeBanner title="This browser can't show push notifications." variant="status" />)

    expect(screen.getByRole('status')).toHaveTextContent("This browser can't show push notifications.")
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
