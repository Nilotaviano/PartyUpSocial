import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import '../../i18n'
import { SessionSummaryCard } from './SessionSummaryCard'
import type { SessionSummaryResponse } from './lovable/sessionData'

function session(overrides: Partial<SessionSummaryResponse> = {}): SessionSummaryResponse {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 90,
    platform: 'steam',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    status: 'confirmed',
    isPrivate: false,
    maxCapacity: 8,
    activeOccupancy: 3,
    organizer: { ordinal: 0, displayName: 'Alex Chen', avatar: null },
    viewerRelation: 'organizer',
    ...overrides,
  } as SessionSummaryResponse
}

function renderCard(props: Partial<React.ComponentProps<typeof SessionSummaryCard>> = {}) {
  return render(
    <MemoryRouter>
      <ul>
        <SessionSummaryCard session={session()} timeZone="UTC" {...props} />
      </ul>
    </MemoryRouter>,
  )
}

afterEach(() => {
  cleanup()
})

describe('SessionSummaryCard', () => {
  it('links to the session detail route by title', () => {
    renderCard()

    expect(screen.getByRole('link', { name: /Raid night/ })).toHaveAttribute(
      'href',
      '/sessions/session-1',
    )
  })

  it('renders game, platform, and occupancy off the same summary — the fields Home/Discover previously dropped', () => {
    renderCard()

    expect(screen.getByText('Deep Rock Galactic')).toBeInTheDocument()
    expect(screen.getByText('3/8 going')).toBeInTheDocument()
  })

  it('renders both the start time and the computed end time, closing the HIGH duration-never-rendered gap', () => {
    renderCard()

    const endText = new Intl.DateTimeFormat('en', { timeStyle: 'short', timeZone: 'UTC' }).format(
      new Date('2030-09-01T19:30:00Z'),
    )
    expect(screen.getByText(`Ends ${endText}`)).toBeInTheDocument()
  })

  it('shows the in-progress chip only when the caller opts in via showInProgress', () => {
    const now = Date.now()
    renderCard({
      session: session({ startTime: new Date(now - 60_000).toISOString(), durationMinutes: 60 }),
      showInProgress: true,
    })

    expect(screen.getByText('In progress')).toBeInTheDocument()
  })

  it('omits the in-progress chip by default even for a currently running session', () => {
    const now = Date.now()
    renderCard({
      session: session({ startTime: new Date(now - 60_000).toISOString(), durationMinutes: 60 }),
    })

    expect(screen.queryByText('In progress')).not.toBeInTheDocument()
  })

  it('masks the organizer identity when the wire withholds it_D076', () => {
    renderCard({
      session: session({ organizerId: null, organizer: { ordinal: 0, displayName: null, avatar: null } }),
    })

    expect(screen.getByText('Anonymous User')).toBeInTheDocument()
  })
})
