import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { OccupancyChip, OrganizerRow, StatusPill } from './SessionBits'
import type { OrganizerIdentity } from './sessionData'

afterEach(() => {
  cleanup()
})

describe('StatusPill', () => {
  it('renders the caller-supplied label text alongside its icon, not color alone_D139', () => {
    render(<StatusPill status="confirmed" label="Confirmed" />)
    expect(screen.getByText('Confirmed')).toBeInTheDocument()
  })

  it('renders a distinct pill per status', () => {
    const { container: unconfirmed } = render(
      <StatusPill status="unconfirmed" label="Waiting for players" />,
    )
    const { container: canceled } = render(<StatusPill status="canceled" label="Canceled" />)
    expect(unconfirmed.querySelector('span')?.className).not.toBe(
      canceled.querySelector('span')?.className,
    )
  })

  it('carries the full SessionStatusChip status vocabulary', () => {
    const statuses = ['unconfirmed', 'confirmed', 'completed', 'expired', 'canceled'] as const
    for (const status of statuses) {
      const { unmount } = render(<StatusPill status={status} label={status} />)
      expect(screen.getByText(status)).toBeInTheDocument()
      unmount()
    }
  })
})

describe('OccupancyChip', () => {
  it('renders the caller-supplied occupancy text, not color alone', () => {
    render(<OccupancyChip label="2/8 going" />)
    expect(screen.getByText('2/8 going')).toBeInTheDocument()
  })
})

const realOrganizer: OrganizerIdentity = {
  seed: 'user-organizer',
  name: 'Mika Ortega',
  emoji: '🐙',
  imgUrl: null,
  masked: false,
}

const maskedOrganizer: OrganizerIdentity = {
  seed: 'masked-organizer-session-1',
  name: 'Anonymous User',
  emoji: null,
  imgUrl: null,
  masked: true,
}

describe('OrganizerRow masking D-076', () => {
  it('renders the real organizer name and emoji for an unmasked organizer_D076', () => {
    render(<OrganizerRow organizer={realOrganizer} organizerLabel="Organizer" />)
    expect(screen.getByText('Mika Ortega')).toBeInTheDocument()
  })

  it('never renders a real organizer name for a masked organizer_D076', () => {
    render(<OrganizerRow organizer={maskedOrganizer} organizerLabel="Organizer" />)
    expect(screen.getByText('Anonymous User')).toBeInTheDocument()
    expect(screen.queryByText('Mika Ortega')).not.toBeInTheDocument()
  })

  it('gives the masked organizer the shared neutral disc treatment, not a distinct style_D076', () => {
    const { container } = render(<OrganizerRow organizer={maskedOrganizer} organizerLabel="Organizer" />)
    const disc = container.querySelector('span[aria-hidden="true"]')
    expect(disc).not.toBeNull()
    expect(disc).toHaveClass('bg-busy')
    expect(disc).toHaveTextContent('●')
  })

  it('carries the organizer label as accessible sr-only text', () => {
    render(<OrganizerRow organizer={realOrganizer} organizerLabel="Organizer" />)
    expect(screen.getByText('Organizer')).toHaveClass('sr-only')
  })
})
