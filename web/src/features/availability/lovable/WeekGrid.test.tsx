import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../../i18n'
import { WeekGrid } from './WeekGrid'
import type {
  AvailabilityRule,
  Friend,
  GameSession,
  MergedSpan,
  PollOption,
  WeekLabels,
} from './partyup-data'

const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

const labels: WeekLabels = {
  dayNames: DAY_NAMES,
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

type Overrides = Partial<Parameters<typeof WeekGrid>[0]>

function renderGrid(overrides: Overrides = {}) {
  const onDraftComplete = vi.fn()
  const onResize = vi.fn()
  const onSelect = vi.fn()
  const onPageStep = vi.fn()
  const props = {
    rules: [] as AvailabilityRule[],
    merged: [] as MergedSpan[],
    friends: [] as Friend[],
    sessions: [] as GameSession[],
    polls: [] as PollOption[],
    labels,
    days: [0, 1, 2, 3, 4, 5, 6],
    rangeStart: 8 * 60,
    rangeEnd: 24 * 60,
    todayIndex: 1,
    nowMinutes: null,
    selectedId: null,
    onSelect,
    onDraftComplete,
    onResize,
    onPageStep,
    ...overrides,
  }
  render(<WeekGrid {...props} />)
  return { onDraftComplete, onResize, onSelect, onPageStep }
}

/** The cell layer is keyboard-only, so every flow below starts by focusing one. */
function focusCell(name: string) {
  const cell = screen.getByRole('gridcell', { name })
  // `act` so the grid's roving-tab-stop state lands before the next key press
  // reads it — a bare `.focus()` leaves the handler on the previous render.
  act(() => cell.focus())
  return cell
}

afterEach(() => {
  cleanup()
})

describe('WeekGrid keyboard flow', () => {
  it('paints a selection in 15-minute increments from the keyboard alone_D071_D132', () => {
    const { onDraftComplete } = renderGrid()

    const start = focusCell('Mon 09:00')
    fireEvent.keyDown(start, { key: 'Enter' })
    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    fireEvent.keyDown(screen.getByRole('gridcell', { name: 'Mon 09:15' }), { key: 'Enter' })

    expect(onDraftComplete).toHaveBeenCalledTimes(1)
    const draft = onDraftComplete.mock.calls[0][0]
    expect(draft.day).toBe(1)
    // Both edges land on the 15-minute lattice, and the paint covers the two
    // rows the selection actually grew over (09:00 through 09:30).
    expect(draft.start % 15).toBe(0)
    expect(draft.end % 15).toBe(0)
    expect(draft.start).toBe(9 * 60)
    expect(draft.end).toBe(9 * 60 + 30)
  })

  it('mirrors Enter with Space for starting and completing a selection_D071_D132', () => {
    const { onDraftComplete } = renderGrid()

    const start = focusCell('Tue 10:00')
    fireEvent.keyDown(start, { key: ' ' })
    fireEvent.keyDown(start, { key: ' ' })

    expect(onDraftComplete).toHaveBeenCalledTimes(1)
    expect(onDraftComplete.mock.calls[0][0]).toMatchObject({
      day: 2,
      start: 10 * 60,
      end: 10 * 60 + 15,
    })
  })

  it('cancels an in-flight keyboard selection on Escape without painting_D132', () => {
    const { onDraftComplete } = renderGrid()

    const start = focusCell('Mon 09:00')
    fireEvent.keyDown(start, { key: 'Enter' })
    fireEvent.keyDown(start, { key: 'Escape' })
    fireEvent.keyDown(start, { key: 'Enter' })

    // The Escape dropped the anchor, so the last Enter only re-armed it.
    expect(onDraftComplete).not.toHaveBeenCalled()
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 09:00.')
  })

  it('announces the selection extent as it grows and when it is dropped_D139', () => {
    renderGrid()
    const status = screen.getByRole('status')

    const start = focusCell('Mon 09:00')
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(status).toHaveTextContent('Selection started at 09:00.')

    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    const afterOne = status.textContent
    expect(afterOne).toBe('Selected 09:00 to 09:30.')

    fireEvent.keyDown(screen.getByRole('gridcell', { name: 'Mon 09:15' }), {
      key: 'ArrowDown',
      shiftKey: true,
    })
    // A live region that repeated itself would leave the viewer unable to tell
    // the range had widened at all.
    expect(status.textContent).not.toBe(afterOne)
    expect(status).toHaveTextContent('Selected 09:00 to 09:45.')

    fireEvent.keyDown(screen.getByRole('gridcell', { name: 'Mon 09:30' }), { key: 'Escape' })
    expect(status).toHaveTextContent('Selection canceled.')
  })

  it('keeps exactly one roving tab stop and moves it with the arrow keys_D132', () => {
    renderGrid()

    const tabStops = () =>
      screen.getAllByRole('gridcell').filter((c) => c.getAttribute('tabindex') === '0')
    expect(tabStops()).toHaveLength(1)

    const cell = focusCell('Mon 09:00')
    fireEvent.keyDown(cell, { key: 'ArrowRight' })

    expect(tabStops()).toHaveLength(1)
    expect(document.activeElement).toBe(screen.getByRole('gridcell', { name: 'Tue 09:00' }))
  })

  it('pages the shown day window when an arrow walks past its edge_S5', () => {
    const { onPageStep } = renderGrid({ days: [2, 3, 4] })

    const cell = focusCell('Tue 09:00')
    fireEvent.keyDown(cell, { key: 'ArrowLeft' })
    expect(onPageStep).toHaveBeenCalledWith(-1)

    const last = focusCell('Thu 09:00')
    fireEvent.keyDown(last, { key: 'ArrowRight' })
    expect(onPageStep).toHaveBeenCalledWith(1)
  })

  it('exposes the shown days only, in window-relative column order_S5', () => {
    renderGrid({ days: [2, 3, 4] })

    const row = screen.getAllByRole('row')[0]
    const cells = row.querySelectorAll('[role="gridcell"]')
    expect(cells).toHaveLength(3)
    expect(cells[0].getAttribute('aria-colindex')).toBe('2')
    expect(cells[0].getAttribute('aria-label')).toBe('Tue 08:00')
    expect(screen.queryByRole('gridcell', { name: 'Mon 08:00' })).not.toBeInTheDocument()
  })
})

describe('WeekGrid pointer flow', () => {
  /** jsdom lays nothing out, so the painted column is given a real box. */
  function stubColumn(day: number) {
    const column = document.querySelector<HTMLElement>(`[data-daycol="${day}"]`)!
    // 64 rows of 15px over 08:00–24:00, so 1px === 1 minute.
    vi.spyOn(column, 'getBoundingClientRect').mockReturnValue({
      top: 0,
      left: 0,
      bottom: 960,
      right: 100,
      width: 100,
      height: 960,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    } as DOMRect)
    return column
  }

  it('snaps a mouse-drag paint to 15-minute increments_D071', () => {
    const { onDraftComplete } = renderGrid()
    const column = stubColumn(1)

    fireEvent.pointerDown(column, { button: 0, clientY: 37 })
    fireEvent.pointerMove(window, { clientY: 112 })
    fireEvent.pointerUp(window, { clientX: 20, clientY: 112 })

    expect(onDraftComplete).toHaveBeenCalledTimes(1)
    const draft = onDraftComplete.mock.calls[0][0]
    expect(draft.day).toBe(1)
    expect(draft.start % 15).toBe(0)
    expect(draft.end % 15).toBe(0)
    // 08:37 and 09:52 off the raw pointer, snapped down/up to the lattice.
    expect(draft.start).toBe(8 * 60 + 30)
    expect(draft.end).toBe(9 * 60 + 45)
  })

  it('does not paint from a press that starts on an existing band_S4', () => {
    const { onDraftComplete, onSelect } = renderGrid({
      rules: [{ id: 'block-a', kind: 'oneoff', days: [1], start: 540, end: 660, intent: 'free' }],
      merged: [{ day: 1, start: 540, end: 660 }],
    })
    stubColumn(1)

    fireEvent.pointerDown(screen.getByRole('button', { name: /09:00/ }), { button: 0, clientY: 60 })
    fireEvent.pointerUp(window, { clientX: 20, clientY: 60 })

    expect(onSelect).toHaveBeenCalledWith('block-a')
    expect(onDraftComplete).not.toHaveBeenCalled()
  })

  it('reports the touch long-press state the drag gate depends on_P5TOUCH01', () => {
    vi.useFakeTimers()
    try {
      const { onDraftComplete } = renderGrid()
      const column = stubColumn(1)
      const root = document.querySelector('[data-grid-root]')!

      fireEvent.pointerDown(column, { button: 0, pointerType: 'touch', clientY: 60 })
      // Before the long-press threshold a finger drag must still scroll.
      expect(root).toHaveAttribute('data-touch-dragging', 'false')

      act(() => {
        vi.advanceTimersByTime(400)
      })
      expect(root).toHaveAttribute('data-touch-dragging', 'true')

      fireEvent.pointerUp(window, { clientX: 20, clientY: 60 })
      expect(onDraftComplete).toHaveBeenCalledTimes(1)
      expect(root).toHaveAttribute('data-touch-dragging', 'false')
    } finally {
      vi.useRealTimers()
    }
  })

  it('commits nothing when a long press turns into a scroll_P5TOUCH01', () => {
    vi.useFakeTimers()
    try {
      const { onDraftComplete } = renderGrid()
      const column = stubColumn(1)
      const root = document.querySelector('[data-grid-root]')!

      fireEvent.pointerDown(column, { button: 0, pointerType: 'touch', clientY: 60 })
      fireEvent.pointerMove(column, { clientY: 200 })
      act(() => {
        vi.advanceTimersByTime(400)
      })

      expect(root).toHaveAttribute('data-touch-dragging', 'false')
      expect(onDraftComplete).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('WeekGrid layers', () => {
  it('paints busy state from the server merge, never re-derived from occurrences_D101', () => {
    // The true union of these two rules is 09:00–09:45, so a fixture using that
    // union could not tell a client trusting `merged` (A-076) apart from one
    // silently recomputing it. `merged` is deliberately short of rule-b's tail.
    const rules: AvailabilityRule[] = [
      { id: 'block-a', kind: 'oneoff', days: [1], start: 540, end: 570, intent: 'free' },
      { id: 'block-b', kind: 'oneoff', days: [1], start: 555, end: 585, intent: 'free' },
    ]
    const merged: MergedSpan[] = [{ day: 1, start: 540, end: 570 }]
    const { onSelect } = renderGrid({ rules, merged })

    for (const time of ['09:00', '09:15']) {
      expect(screen.getByRole('gridcell', { name: `Mon ${time} – Busy` })).toHaveAttribute(
        'data-state',
        'busy',
      )
    }
    // 09:30 is still covered by rule-b as an occurrence; a client re-deriving
    // the union would paint it busy and fail here.
    expect(screen.getByRole('gridcell', { name: 'Mon 09:30' })).toHaveAttribute(
      'data-state',
      'free',
    )

    // Both rules stay separately addressable, including the one `merged` does
    // not fully cover.
    const bands = screen.getAllByRole('button', { name: /09:/ })
    fireEvent.pointerDown(bands[0])
    expect(onSelect).toHaveBeenCalledWith('block-a')
    fireEvent.pointerDown(bands[1])
    expect(onSelect).toHaveBeenCalledWith('block-b')
  })

  it('paints friend, session and poll layers without adding gridcells_D111_A080', () => {
    const baseline = (() => {
      renderGrid()
      const count = screen.getAllByRole('gridcell').length
      cleanup()
      return count
    })()

    renderGrid({
      friends: [
        {
          id: 'friend-1',
          name: 'Ana Silva',
          initials: 'AS',
          chip: 1,
          windows: [{ day: 1, start: 540, end: 570, intent: 'free' }],
        },
      ],
      sessions: [
        { id: 'session-1', title: 'Raid night', day: 1, start: 600, end: 720, occupancy: 3 },
      ],
      polls: [
        {
          id: 'poll-option-1',
          pollTitle: 'Deep Rock co-op',
          day: 2,
          start: 660,
          end: 720,
          voteLabel: 'your vote: available',
        },
      ],
    })

    // Every layer reads as text, never colour alone (WCAG 1.4.1).
    expect(screen.getByText(/Raid night/)).toBeInTheDocument()
    expect(screen.getByText(/Deep Rock co-op/)).toBeInTheDocument()
    expect(screen.getByText(/your vote: available/)).toBeInTheDocument()
    expect(screen.getByTitle(/Ana Silva/)).toBeInTheDocument()

    // The layers are painted over the same slots — they never become cells of
    // their own, so the cell layer's shape is untouched.
    expect(screen.getAllByRole('gridcell')).toHaveLength(baseline)
    expect(
      screen.getAllByRole('gridcell').filter((c) => c.getAttribute('tabindex') === '0'),
    ).toHaveLength(1)
  })

  it('marks the now-line on today only, and only inside the visible hours_S6', () => {
    const { container } = render(
      <div>
        <WeekGrid
          rules={[]}
          merged={[]}
          friends={[]}
          sessions={[]}
          polls={[]}
          labels={labels}
          days={[0, 1, 2, 3, 4, 5, 6]}
          rangeStart={8 * 60}
          rangeEnd={24 * 60}
          todayIndex={1}
          nowMinutes={13 * 60}
          selectedId={null}
          onSelect={vi.fn()}
          onDraftComplete={vi.fn()}
          onResize={vi.fn()}
        />
      </div>,
    )
    const columns = container.querySelectorAll('[data-daycol]')
    expect(columns[1].querySelector('.bg-destructive')).not.toBeNull()
    expect(columns[0].querySelector('.bg-destructive')).toBeNull()
  })

  it('suppresses the now-line when the shown week is not the current one_D046', () => {
    const { container } = render(
      <WeekGrid
        rules={[]}
        merged={[]}
        friends={[]}
        sessions={[]}
        polls={[]}
        labels={labels}
        days={[0, 1, 2, 3, 4, 5, 6]}
        rangeStart={8 * 60}
        rangeEnd={24 * 60}
        todayIndex={-1}
        nowMinutes={null}
        selectedId={null}
        onSelect={vi.fn()}
        onDraftComplete={vi.fn()}
        onResize={vi.fn()}
      />,
    )
    expect(container.querySelector('.bg-destructive')).toBeNull()
  })
})

describe('WeekGrid resize handles', () => {
  const rules: AvailabilityRule[] = [
    { id: 'block-a', kind: 'oneoff', days: [1], start: 540, end: 660, intent: 'free' },
  ]

  it('hides the resize handles until the band is selected_S4', () => {
    renderGrid({ rules })
    expect(screen.queryByRole('button', { name: 'Change start time' })).not.toBeInTheDocument()

    cleanup()
    renderGrid({ rules, selectedId: 'block-a' })
    expect(screen.getByRole('button', { name: 'Change start time' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Change end time' })).toBeInTheDocument()
  })

  it('nudges an edge by one 15-minute slot from the keyboard_D071_D025', () => {
    const { onResize } = renderGrid({ rules, selectedId: 'block-a' })

    fireEvent.keyDown(screen.getByRole('button', { name: 'Change end time' }), {
      key: 'ArrowDown',
    })
    expect(onResize).toHaveBeenCalledWith('block-a', 540, 675)

    fireEvent.keyDown(screen.getByRole('button', { name: 'Change start time' }), {
      key: 'ArrowUp',
    })
    expect(onResize).toHaveBeenCalledWith('block-a', 525, 660)
  })

  it('never lets an edge cross its opposite edge_D071', () => {
    const short: AvailabilityRule[] = [
      { id: 'block-a', kind: 'oneoff', days: [1], start: 540, end: 555, intent: 'free' },
    ]
    const { onResize } = renderGrid({ rules: short, selectedId: 'block-a' })

    fireEvent.keyDown(screen.getByRole('button', { name: 'Change start time' }), {
      key: 'ArrowDown',
    })
    // Floored at end - one slot rather than swapping past it.
    expect(onResize).toHaveBeenCalledWith('block-a', 540, 555)
  })

  it('swallows Enter and Space on a handle so it cannot re-select the band_S4', () => {
    const { onSelect } = renderGrid({ rules, selectedId: 'block-a' })

    const handle = screen.getByRole('button', { name: 'Change start time' })
    const enter = fireEvent.keyDown(handle, { key: 'Enter' })
    const space = fireEvent.keyDown(handle, { key: ' ' })

    expect(enter).toBe(false)
    expect(space).toBe(false)
    expect(onSelect).not.toHaveBeenCalled()
  })
})
