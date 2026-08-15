import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'

import { PageHeader } from './PageHeader'

afterEach(cleanup)

describe('shell PageHeader', () => {
  it('focuses the page h1 on mount_D139', () => {
    render(
      <MemoryRouter>
        <PageHeader title="Sessions" />
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { level: 1, name: 'Sessions' })).toHaveFocus()
  })

  it('renders eyebrow, description and actions', () => {
    render(
      <MemoryRouter>
        <PageHeader
          actions={<button type="button">New session</button>}
          description="Everything coming up."
          eyebrow="Game night"
          title="Sessions"
        />
      </MemoryRouter>,
    )

    expect(screen.getByText('Game night')).toBeInTheDocument()
    expect(screen.getByText('Everything coming up.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'New session' })).toBeInTheDocument()
  })

  it('renders an optional back link at a 44px target', () => {
    render(
      <MemoryRouter>
        <PageHeader back={{ to: '/sessions/upcoming', label: 'Back to sessions' }} title="Detail" />
      </MemoryRouter>,
    )

    const back = screen.getByRole('link', { name: 'Back to sessions' })
    expect(back).toHaveAttribute('href', '/sessions/upcoming')
    expect(back.className).toContain('h-11')
  })

  it('renders no back link when omitted', () => {
    render(
      <MemoryRouter>
        <PageHeader title="Detail" />
      </MemoryRouter>,
    )

    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('centers the column when align is center', () => {
    render(
      <MemoryRouter>
        <PageHeader align="center" title="Sign in" />
      </MemoryRouter>,
    )

    const heading = screen.getByRole('heading', { level: 1, name: 'Sign in' })
    expect(heading.closest('div')?.parentElement?.className).toContain('items-center')
  })

  it('sets aria-live only when live is requested', () => {
    render(
      <MemoryRouter>
        <PageHeader live title="Live title" />
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { level: 1 })).toHaveAttribute('aria-live', 'polite')
  })
})
