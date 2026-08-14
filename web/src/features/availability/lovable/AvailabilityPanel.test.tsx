import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../../i18n'
import { AvailabilityPanel } from './AvailabilityPanel'
import type { AvailabilityRule, WeekLabels } from './partyup-data'

const labels: WeekLabels = {
  dayNames: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
  dayLongNames: [
    'Sunday',
    'Monday',
    'Tuesday',
    'Wednesday',
    'Thursday',
    'Friday',
    'Saturday',
  ],
  dayDates: ['9 Aug', '10 Aug', '11 Aug', '12 Aug', '13 Aug', '14 Aug', '15 Aug'],
}

const weekly: AvailabilityRule = {
  id: 'series-1:2026-08-13',
  kind: 'recurring',
  days: [4],
  start: 19 * 60 + 30,
  end: 22 * 60,
  intent: 'free',
}

const oneOff: AvailabilityRule = {
  id: 'block-1',
  kind: 'oneoff',
  days: [2],
  start: 20 * 60,
  end: 23 * 60,
  intent: 'free',
}

function renderPanel(rules: AvailabilityRule[]) {
  const onDelete = vi.fn()
  const onUpdate = vi.fn()
  const onSelect = vi.fn()
  render(
    <AvailabilityPanel
      labels={labels}
      onDelete={onDelete}
      onSelect={onSelect}
      onUpdate={onUpdate}
      rules={rules}
      selectedId={null}
    />,
  )
  return { onDelete, onUpdate, onSelect }
}

afterEach(() => {
  cleanup()
})

describe('AvailabilityPanel', () => {
  it('lists weekly and one-off rules as readable text under their own headings_D025', () => {
    renderPanel([weekly, oneOff])

    expect(screen.getByText('Thu · 19:30–22:00')).toBeInTheDocument()
    expect(screen.getByText('Tue 11 Aug · 20:00–23:00')).toBeInTheDocument()
    expect(screen.getByText('Repeats weekly')).toBeInTheDocument()
    expect(screen.getByText('This week only')).toBeInTheDocument()
    // Kind is a word on the row, never colour or icon alone (WCAG 1.4.1).
    expect(screen.getByText(/Free · Repeats weekly/)).toBeInTheDocument()
    expect(screen.getByText(/Free · One-time/)).toBeInTheDocument()
  })

  it('states the empty one-off group rather than showing nothing_S4', () => {
    renderPanel([weekly])
    expect(screen.getByText('No one-off windows yet.')).toBeInTheDocument()
  })

  it('deletes a one-off window without a confirm step_D025', () => {
    const { onDelete } = renderPanel([oneOff])

    fireEvent.click(screen.getByRole('button', { name: 'Delete window' }))

    expect(onDelete).toHaveBeenCalledWith('block-1')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('asks before deleting a weekly rule, and says it covers future weeks_D025', () => {
    const { onDelete } = renderPanel([weekly])

    fireEvent.click(screen.getByRole('button', { name: 'Delete window' }))
    expect(onDelete).not.toHaveBeenCalled()

    const dialog = screen.getByRole('alertdialog')
    expect(within(dialog).getByText('Delete this weekly rule?')).toBeInTheDocument()
    expect(within(dialog).getByText(/all future weeks/)).toBeInTheDocument()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete rule' }))
    expect(onDelete).toHaveBeenCalledWith('series-1:2026-08-13')
  })

  it('keeps the weekly rule when the confirm is dismissed_D025', () => {
    const { onDelete } = renderPanel([weekly])

    fireEvent.click(screen.getByRole('button', { name: 'Delete window' }))
    fireEvent.click(screen.getByRole('button', { name: 'Keep it' }))

    expect(onDelete).not.toHaveBeenCalled()
    expect(screen.getByText('Thu · 19:30–22:00')).toBeInTheDocument()
  })

  it('edits a window through 15-minute time choices only_D071', () => {
    const { onUpdate } = renderPanel([oneOff])

    fireEvent.click(screen.getByRole('button', { name: 'Edit window' }))
    const start = screen.getByRole('combobox', { name: 'Start time' })
    const options = within(start).getAllByRole('option')
    // 00:00 through 24:00 inclusive, one entry per 15-minute slot.
    expect(options).toHaveLength(97)
    expect(options.every((o) => Number((o as HTMLOptionElement).value) % 15 === 0)).toBe(true)

    fireEvent.change(start, { target: { value: String(19 * 60) } })
    expect(onUpdate).toHaveBeenCalledWith('block-1', 19 * 60, 23 * 60)

    fireEvent.change(screen.getByRole('combobox', { name: 'End time' }), {
      target: { value: String(21 * 60 + 45) },
    })
    expect(onUpdate).toHaveBeenLastCalledWith('block-1', 20 * 60, 21 * 60 + 45)
  })

  it('never lets an edited end land before its start_D071', () => {
    const { onUpdate } = renderPanel([oneOff])

    fireEvent.click(screen.getByRole('button', { name: 'Edit window' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'End time' }), {
      target: { value: String(8 * 60) },
    })

    expect(onUpdate).toHaveBeenCalledWith('block-1', 20 * 60, 20 * 60 + 15)
  })

  it('selects and deselects the rule the grid highlights_S4', () => {
    const { onSelect } = renderPanel([oneOff])

    fireEvent.click(screen.getByText('Tue 11 Aug · 20:00–23:00'))
    expect(onSelect).toHaveBeenCalledWith('block-1')
  })
})
