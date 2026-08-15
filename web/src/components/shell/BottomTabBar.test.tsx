import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'

import '../../i18n'
import { BottomTabBar } from './BottomTabBar'

function renderTabs(initialEntry: string) {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <BottomTabBar />
    </MemoryRouter>,
  )
}

afterEach(cleanup)

describe('BottomTabBar', () => {
  it('renders five equal section links under one landmark', () => {
    renderTabs('/app')

    const nav = screen.getByRole('navigation', { name: 'Section tabs' })
    expect(nav).toBeInTheDocument()
    for (const label of ['Home', 'Friends', 'Sessions', 'Polls', 'Calendar']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument()
    }
  })

  it('marks Polls active via aria-current for a poll detail sub-route', () => {
    renderTabs('/polls/00000000-0000-0000-0000-000000000003')

    expect(screen.getByRole('link', { name: 'Polls' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Home' })).not.toHaveAttribute('aria-current')
  })

  it('never renders color as the only active signal — bold label follows aria-current', () => {
    renderTabs('/friends')

    const friendsLabel = screen.getByRole('link', { name: 'Friends' }).querySelector('span:last-child')
    expect(friendsLabel?.className).toContain('font-extrabold')
  })
})
