import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import '../../i18n'
import { AgendaView, type AgendaEntry } from './AgendaView'

function fakeLocal(hour: number, minute = 0, day = 10) {
  return new Date(Date.UTC(2026, 7, day, hour, minute, 0, 0))
}

function timeLabelFormatter(date: Date) {
  return `${date.getUTCHours()}:${String(date.getUTCMinutes()).padStart(2, '0')}`
}

function dateLabelFormatter(date: Date) {
  return `Aug ${date.getUTCDate()}`
}

afterEach(() => {
  cleanup()
})

describe('AgendaView', () => {
  it('renders the chronological agenda over the same range as the week grid_D104', () => {
    // Deliberately out of chronological order and spanning the same range
    // the week grid would show (CalendarShell feeds both from one
    // `GET /calendar` response, never a second fetch).
    const entries: AgendaEntry[] = [
      {
        id: 'session-1',
        kind: 'session',
        start: fakeLocal(18, 0, 12),
        end: fakeLocal(20, 0, 12),
        title: 'Raid night',
      },
      {
        id: 'availability-0',
        kind: 'availability',
        start: fakeLocal(9, 0, 10),
        end: fakeLocal(10, 0, 10),
        title: 'My availability',
      },
      {
        id: 'availability-1',
        kind: 'availability',
        start: fakeLocal(9, 0, 11),
        end: fakeLocal(11, 0, 11),
        title: 'My availability',
      },
    ]

    render(
      <AgendaView
        dateLabelFormatter={dateLabelFormatter}
        entries={entries}
        timeLabelFormatter={timeLabelFormatter}
      />,
    )

    const items = screen.getAllByRole('listitem')
    expect(items).toHaveLength(3)
    // Chronological, not insertion order.
    expect(within(items[0]).getByText('Aug 10')).toBeInTheDocument()
    expect(within(items[0]).getByText('9:00–10:00')).toBeInTheDocument()
    expect(within(items[1]).getByText('Aug 11')).toBeInTheDocument()
    expect(within(items[2]).getByText('Aug 12')).toBeInTheDocument()
    expect(within(items[2]).getByText('Raid night')).toBeInTheDocument()
  })

  it('renders an empty-range message rather than an empty list', () => {
    render(
      <AgendaView dateLabelFormatter={dateLabelFormatter} entries={[]} timeLabelFormatter={timeLabelFormatter} />,
    )

    expect(screen.queryByRole('list')).not.toBeInTheDocument()
    expect(screen.getByText('Nothing scheduled in this range.')).toBeInTheDocument()
  })
})
