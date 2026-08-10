import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import { AvailabilitySummary } from './AvailabilitySummary'
import type { AvailabilitySeriesView } from './availabilityQueries'

afterEach(cleanup)

// `dayOfWeek` is NodaTime's IsoDayOfWeek on the wire (Monday=1 … Sunday=7),
// not the JS 0=Sunday convention — 2 is Tuesday.
const series: AvailabilitySeriesView[] = [
  {
    seriesId: 'series-1',
    dayOfWeek: 2,
    startTime: '20:00',
    durationMinutes: 180,
    zoneId: 'America/Sao_Paulo',
    effectiveFrom: '2026-08-01',
    effectiveUntil: null,
  },
]

describe('AvailabilitySummary', () => {
  it('renders a weekly series as a readable rule_D025', () => {
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={series}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={vi.fn()}
      />,
    )

    expect(screen.getByText(/Tue/)).toBeInTheDocument()
    expect(screen.getByText(/20:00/)).toBeInTheDocument()
    expect(screen.getByText(/23:00/)).toBeInTheDocument()
  })

  it('deletes the whole series after confirming_D025', () => {
    const onDeleteSeries = vi.fn()
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={series}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={onDeleteSeries}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /Delete/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Delete series' }))

    expect(onDeleteSeries).toHaveBeenCalledWith('series-1')
  })

  it('keeps the series when the confirm is dismissed_D025', () => {
    const onDeleteSeries = vi.fn()
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={series}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={onDeleteSeries}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /Delete/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Keep it' }))

    expect(onDeleteSeries).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('shows a series that has no occurrence in the visible week_D025', () => {
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={[{ ...series[0], effectiveFrom: '2099-01-01' }]}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={vi.fn()}
      />,
    )

    expect(screen.getByText(/Tue/)).toBeInTheDocument()
  })

  it('deletes a one-off block without a confirm step_D025', () => {
    const onDeleteBlock = vi.fn()
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[
          {
            blockId: 'block-1',
            startTime: '2026-08-11T23:00:00.000Z',
            endTime: '2026-08-12T02:00:00.000Z',
          },
        ]}
        series={[]}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={onDeleteBlock}
        onDeleteSeries={vi.fn()}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /Delete/ }))

    expect(onDeleteBlock).toHaveBeenCalledWith('block-1')
  })

  it('tells the viewer when nothing is published yet_D025', () => {
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={[]}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={vi.fn()}
      />,
    )

    expect(
      screen.getByText('You have not published any availability yet.'),
    ).toBeInTheDocument()
  })
})
