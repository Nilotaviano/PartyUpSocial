import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { StatusPill, VoterAvatar, VoterRow } from './PollBits'
import { VOTE_META, type Voter } from './pollData'

afterEach(() => {
  cleanup()
})

const realVoter: Voter = {
  id: 'p-1',
  name: 'Mika Ortega',
  seed: 'p-1',
  emoji: '🐙',
  imgUrl: null,
  masked: false,
  isActive: true,
}

const maskedVoter: Voter = {
  id: 'p-2',
  name: 'Anonymous User',
  seed: 'p-2',
  emoji: null,
  imgUrl: null,
  masked: true,
  isActive: true,
}

describe('VoterAvatar/VoterRow masking D-076', () => {
  it('never renders a real voter name or emoji for a masked voter_D076', () => {
    render(<VoterRow voter={maskedVoter} maskedSuffix="(hidden)" />)

    expect(screen.getAllByText('Anonymous User').length).toBeGreaterThan(0)
    expect(screen.getByText('(hidden)')).toBeInTheDocument()
    expect(screen.queryByText('Mika Ortega')).not.toBeInTheDocument()
    expect(screen.queryByText('🐙')).not.toBeInTheDocument()
  })

  it('renders the real name and no masked suffix for an unmasked voter_D076', () => {
    render(<VoterRow voter={realVoter} maskedSuffix="(hidden)" />)

    expect(screen.getAllByText('Mika Ortega').length).toBeGreaterThan(0)
    expect(screen.queryByText('(hidden)')).not.toBeInTheDocument()
  })

  it('gives the masked voter the shared neutral disc treatment, not a distinct style_D076', () => {
    const { container } = render(<VoterAvatar voter={maskedVoter} />)
    const disc = container.querySelector('span[aria-hidden="true"]')
    expect(disc).not.toBeNull()
    expect(disc).toHaveClass('bg-busy')
    expect(disc).toHaveTextContent('●')
  })
})

describe('VOTE_META vote-state coding D-139', () => {
  it('exposes a distinct icon per vote value, not color alone', () => {
    expect(VOTE_META.available.icon).not.toBe(VOTE_META.maybe.icon)
    expect(VOTE_META.maybe.icon).not.toBe(VOTE_META.unavailable.icon)
    expect(VOTE_META.available.icon).not.toBe(VOTE_META.unavailable.icon)
  })

  it('every vote value carries its own fill class', () => {
    expect(VOTE_META.available.cls).toContain('bg-free')
    expect(VOTE_META.maybe.cls).toContain('bg-maybe')
    expect(VOTE_META.unavailable.cls).toContain('bg-busy')
  })
})

describe('StatusPill', () => {
  it('renders the caller-supplied label text alongside its icon, not color alone_D139', () => {
    render(<StatusPill status="polling" label="Polling" />)
    expect(screen.getByText('Polling')).toBeInTheDocument()
  })

  it('renders a distinct pill per status', () => {
    const { container: polling } = render(<StatusPill status="polling" label="Polling" />)
    const { container: canceled } = render(<StatusPill status="canceled" label="Canceled" />)
    expect(polling.querySelector('span')?.className).not.toBe(
      canceled.querySelector('span')?.className,
    )
  })
})
