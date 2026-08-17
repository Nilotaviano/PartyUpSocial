import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { VisibilityControl } from './VisibilityControl'

afterEach(() => {
  cleanup()
})

const GROUPS = [
  { id: 'g1', name: 'D&D crew' },
  { id: 'g2', name: 'FPS squad' },
]

describe('VisibilityControl', () => {
  it('renders the three visibility options as a radiogroup', () => {
    render(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={[]}
        value="public"
        onChange={() => {}}
        onToggleGroup={() => {}}
      />,
    )

    expect(screen.getByRole('radiogroup')).toBeInTheDocument()
    const radios = screen.getAllByRole('radio')
    expect(radios).toHaveLength(3)
    expect(screen.getByRole('radio', { name: 'Private' })).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByRole('radio', { name: 'Groups' })).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByRole('radio', { name: 'Public' })).toHaveAttribute('aria-checked', 'true')
  })

  it('reveals the group chip picker only when value is groups, and fires onChange on click', () => {
    const onChange = vi.fn()
    const { rerender } = render(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={[]}
        value="public"
        onChange={onChange}
        onToggleGroup={() => {}}
      />,
    )

    expect(screen.queryByText('D&D crew')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: 'Groups' }))
    expect(onChange).toHaveBeenCalledWith('groups')

    rerender(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={[]}
        value="groups"
        onChange={onChange}
        onToggleGroup={() => {}}
      />,
    )

    expect(screen.getByText('D&D crew')).toBeInTheDocument()
    expect(screen.getByText('FPS squad')).toBeInTheDocument()
  })

  it('toggles a chip via aria-pressed and calls onToggleGroup with its id', () => {
    const onToggleGroup = vi.fn()
    render(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={['g1']}
        value="groups"
        onChange={() => {}}
        onToggleGroup={onToggleGroup}
      />,
    )

    const chip1 = screen.getByRole('button', { name: /D&D crew/ })
    const chip2 = screen.getByRole('button', { name: /FPS squad/ })
    expect(chip1).toHaveAttribute('aria-pressed', 'true')
    expect(chip2).toHaveAttribute('aria-pressed', 'false')

    fireEvent.click(chip2)
    expect(onToggleGroup).toHaveBeenCalledWith('g2')
  })

  it('shows the destructive "pick at least one" hint at zero selections, and the count otherwise_D201', () => {
    const { rerender } = render(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={[]}
        value="groups"
        onChange={() => {}}
        onToggleGroup={() => {}}
      />,
    )

    expect(screen.getByText('Pick at least one group.')).toBeInTheDocument()

    rerender(
      <VisibilityControl
        groups={GROUPS}
        selectedGroupIds={['g1']}
        value="groups"
        onChange={() => {}}
        onToggleGroup={() => {}}
      />,
    )

    expect(screen.queryByText('Pick at least one group.')).not.toBeInTheDocument()
    expect(screen.getByText('1 selected')).toBeInTheDocument()
  })
})
