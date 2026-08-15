import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { Vote } from 'lucide-react'

import { EmptyState, SectionCard } from './SectionCard'

afterEach(cleanup)

describe('SectionCard', () => {
  it('renders the icon, title, action and children', () => {
    render(
      <SectionCard action={<button type="button">See all</button>} icon={Vote} title="Open polls">
        <p>Poll list</p>
      </SectionCard>,
    )

    expect(screen.getByRole('heading', { level: 2, name: 'Open polls' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'See all' })).toBeInTheDocument()
    expect(screen.getByText('Poll list')).toBeInTheDocument()
  })
})

describe('EmptyState', () => {
  it('renders the icon disc, title, description and optional action', () => {
    render(
      <EmptyState
        action={<button type="button">Create a poll</button>}
        description="Nothing here yet."
        icon={Vote}
        title="No polls"
      />,
    )

    expect(screen.getByRole('heading', { level: 3, name: 'No polls' })).toBeInTheDocument()
    expect(screen.getByText('Nothing here yet.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create a poll' })).toBeInTheDocument()
  })

  it('renders without an action', () => {
    render(<EmptyState description="Nothing here yet." icon={Vote} title="No polls" />)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('renders without a description', () => {
    render(<EmptyState icon={Vote} title="No polls" />)

    expect(screen.getByText('No polls')).toBeInTheDocument()
    expect(screen.queryByText('Nothing here yet.')).not.toBeInTheDocument()
  })
})
