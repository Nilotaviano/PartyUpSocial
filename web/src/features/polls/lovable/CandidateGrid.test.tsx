import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../../i18n'
import { CandidateGrid } from './CandidateGrid'
import { candidateKey, type Candidate } from './pollData'
import type {
  Friend,
  GameSession,
  WeekLabels,
} from '../../availability/lovable/partyup-data'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

const labels: WeekLabels = {
  dayNames: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'],
  dayLongNames: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'],
  dayDates: ['10 Aug', '11 Aug', '12 Aug', '13 Aug', '14 Aug', '15 Aug', '16 Aug'],
}

const RANGE_START = 20 * 60
const RANGE_END = 22 * 60

/** The length a tap places before this grid has been told any other one. */
const DEFAULT_MINUTES = 60

function candidate(day: number, start: number, durationMinutes = DEFAULT_MINUTES): Candidate {
  return { day, start, durationMinutes }
}

function baseProps(overrides: Partial<Parameters<typeof CandidateGrid>[0]> = {}) {
  const onCreate = vi.fn()
  const onRemove = vi.fn()
  const onMove = vi.fn()
  const onResize = vi.fn()
  const props = {
    days: [0, 1],
    rangeStart: RANGE_START,
    rangeEnd: RANGE_END,
    step: 30,
    candidates: [] as Candidate[],
    onCreate,
    onRemove,
    onMove,
    onResize,
    max: 12,
    friends: [] as Friend[],
    sessions: [] as GameSession[],
    labels,
    ...overrides,
  }
  return { onCreate, onRemove, onMove, onResize, props }
}

/**
 * jsdom gives every element a zero-sized rect, so the pointer maths that turns
 * a clientY into a minute-of-day would be degenerate. This pins the grid body
 * to one pixel per minute over the rendered window: minute = RANGE_START + y.
 */
function pinRects() {
  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
    top: 0,
    left: 0,
    bottom: RANGE_END - RANGE_START,
    right: 100,
    width: 100,
    height: RANGE_END - RANGE_START,
    x: 0,
    y: 0,
    toJSON: () => ({}),
  } as DOMRect)
}

describe('candidateKey', () => {
  it('is stable and unique per day/start pair', () => {
    expect(candidateKey({ day: 1, start: 90 })).toBe('1-90')
    expect(candidateKey({ day: 1, start: 90 })).not.toBe(candidateKey({ day: 2, start: 90 }))
  })
})

describe('CandidateGrid band rendering', () => {
  it('paints a candidate as one continuous band spanning its own length', () => {
    const { props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    // One band, not two 30-minute pills, and the range label sits at its start.
    expect(band).toHaveTextContent('20:00–21:00')
    expect(band.style.height).toBe('60px')
    expect(band.style.top).toBe('0px')
  })

  it('gives every band its own length, never one shared across the poll_D211', () => {
    const { props } = baseProps({
      candidates: [candidate(0, RANGE_START, 60), candidate(1, RANGE_START + 30, 90)],
    })
    render(<CandidateGrid {...props} />)

    const first = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const second = screen.getByTestId(`candidate-band-1-${RANGE_START + 30}`)
    expect(first.style.height).toBe('60px')
    expect(first).toHaveTextContent('20:00–21:00')
    expect(second.style.height).toBe('90px')
    expect(second).toHaveTextContent('20:30–22:00')
  })
})

describe('CandidateGrid drag-to-paint', () => {
  it('paints a band whose own start and end become one option_D211', () => {
    pinRects()
    const { onCreate, props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
    fireEvent.pointerDown(cell, { button: 0, pointerType: 'mouse', clientY: 0 })
    // One pixel per minute (see `pinRects`): dragging to y=90 is 21:30.
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerUp(window)

    expect(onCreate).toHaveBeenCalledTimes(1)
    expect(onCreate).toHaveBeenCalledWith({
      day: 0,
      start: RANGE_START,
      durationMinutes: 90,
    })
  })

  it('previews the painted band live, upward drags included_D211', () => {
    pinRects()
    const { props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START + 60}`)
    fireEvent.pointerDown(cell, { button: 0, pointerType: 'mouse', clientY: 60 })
    fireEvent.pointerMove(window, { clientY: 15 })

    // The anchor is the press, so dragging up paints 20:15–21:00.
    expect(screen.getByTestId('candidate-paint-0')).toHaveTextContent('20:15–21:00')
  })

  it('hands the painted length forward to the next tap_D211', () => {
    pinRects()
    const { onCreate, props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
    fireEvent.pointerDown(cell, { button: 0, pointerType: 'mouse', clientY: 0 })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerUp(window)

    fireEvent.click(screen.getByTestId(`candidate-slot-1-${RANGE_START + 30}`))

    expect(onCreate).toHaveBeenLastCalledWith({
      day: 1,
      start: RANGE_START + 30,
      durationMinutes: 90,
    })
  })

  it('needs a long press before a finger drag paints anything_D211', () => {
    vi.useFakeTimers()
    try {
      pinRects()
      const { onCreate, props } = baseProps()
      render(<CandidateGrid {...props} />)

      const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
      fireEvent.pointerDown(cell, { button: 0, pointerType: 'touch', clientY: 0 })
      // Before the press arms, the gesture belongs to the page's own scroll.
      fireEvent.pointerMove(window, { clientY: 90 })
      expect(screen.queryByTestId('candidate-paint-0')).toBeNull()

      act(() => vi.advanceTimersByTime(400))
      fireEvent.pointerMove(window, { clientY: 90 })
      fireEvent.pointerUp(window)

      expect(onCreate).toHaveBeenCalledWith({
        day: 0,
        start: RANGE_START,
        durationMinutes: 90,
      })
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('CandidateGrid placement and removal', () => {
  it('places a candidate at the tapped 30-minute cell', () => {
    const { onCreate, props } = baseProps()
    render(<CandidateGrid {...props} />)

    fireEvent.click(screen.getByTestId(`candidate-slot-1-${RANGE_START + 30}`))

    expect(onCreate).toHaveBeenCalledWith({
      day: 1,
      start: RANGE_START + 30,
      durationMinutes: DEFAULT_MINUTES,
    })
  })

  it('removes the candidate a cell already sits under, never adding a duplicate_D080', () => {
    const { onCreate, onRemove, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    // The band spans 20:00–21:00, so the 20:30 cell beneath it removes the
    // same candidate rather than placing a second one.
    fireEvent.click(screen.getByTestId(`candidate-slot-0-${RANGE_START + 30}`))

    expect(onCreate).not.toHaveBeenCalled()
    expect(onRemove).toHaveBeenCalledWith(candidate(0, RANGE_START))
  })

  it('reads coverage from each band own length, not a shared one_D211', () => {
    const { onCreate, onRemove, props } = baseProps({
      candidates: [candidate(0, RANGE_START, 30)],
    })
    render(<CandidateGrid {...props} />)

    // 20:30 is past the end of a 30-minute band, so it is free ground again.
    fireEvent.click(screen.getByTestId(`candidate-slot-0-${RANGE_START + 30}`))

    expect(onRemove).not.toHaveBeenCalled()
    expect(onCreate).toHaveBeenCalledWith({
      day: 0,
      start: RANGE_START + 30,
      durationMinutes: DEFAULT_MINUTES,
    })
  })

  it('removes a candidate when its band is tapped', () => {
    pinRects()
    const { onRemove, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.pointerDown(band, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerUp(window)

    expect(onRemove).toHaveBeenCalledWith(candidate(0, RANGE_START))
  })

  it('stops placing once the option cap is reached, but still removes_D131', () => {
    const { onCreate, onRemove, props } = baseProps({
      candidates: [candidate(0, RANGE_START)],
      max: 1,
    })
    render(<CandidateGrid {...props} />)

    const free = screen.getByTestId(`candidate-slot-1-${RANGE_START}`)
    expect(free).toHaveAttribute('aria-disabled', 'true')
    fireEvent.click(free)
    expect(onCreate).not.toHaveBeenCalled()

    // The occupied cell stays live so a full grid can always be emptied again.
    fireEvent.click(screen.getByTestId(`candidate-slot-0-${RANGE_START}`))
    expect(onRemove).toHaveBeenCalledWith(candidate(0, RANGE_START))
  })

  it('refuses to paint from a capped cell at all_D131', () => {
    pinRects()
    const { onCreate, props } = baseProps({ candidates: [candidate(0, RANGE_START)], max: 1 })
    render(<CandidateGrid {...props} />)

    const free = screen.getByTestId(`candidate-slot-1-${RANGE_START}`)
    fireEvent.pointerDown(free, { button: 0, pointerType: 'mouse', clientY: 0 })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerUp(window)

    expect(onCreate).not.toHaveBeenCalled()
  })

  it('leaves every cell enabled below the cap', () => {
    const { props } = baseProps()
    render(<CandidateGrid {...props} />)

    for (const cell of screen.getAllByRole('gridcell')) {
      expect(cell).not.toHaveAttribute('aria-disabled')
    }
  })
})

describe('CandidateGrid keyboard layer', () => {
  it('places a candidate from the roving gridcell with Enter', () => {
    const { onCreate, props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
    fireEvent.focus(cell)
    fireEvent.keyDown(cell, { key: 'Enter' })

    expect(onCreate).toHaveBeenCalledWith({
      day: 0,
      start: RANGE_START,
      durationMinutes: DEFAULT_MINUTES,
    })
  })

  it('moves a focused band with the arrow keys, carrying its own length', () => {
    const { onMove, props } = baseProps({ candidates: [candidate(0, RANGE_START, 90)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.keyDown(band, { key: 'ArrowDown' })
    expect(onMove).toHaveBeenCalledWith(
      candidate(0, RANGE_START, 90),
      candidate(0, RANGE_START + 30, 90),
    )

    fireEvent.keyDown(band, { key: 'ArrowRight' })
    expect(onMove).toHaveBeenLastCalledWith(
      candidate(0, RANGE_START, 90),
      candidate(1, RANGE_START, 90),
    )
  })

  it('removes a focused band with Enter', () => {
    const { onRemove, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    fireEvent.keyDown(screen.getByTestId(`candidate-band-0-${RANGE_START}`), { key: 'Enter' })
    expect(onRemove).toHaveBeenCalledWith(candidate(0, RANGE_START))
  })

  it('changes only its own band length with Shift and the arrow keys_D211', () => {
    const { onResize, props } = baseProps({
      candidates: [candidate(0, RANGE_START), candidate(1, RANGE_START, 120)],
    })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.keyDown(band, { key: 'ArrowDown', shiftKey: true })
    expect(onResize).toHaveBeenCalledWith(candidate(0, RANGE_START), 75)

    fireEvent.keyDown(band, { key: 'ArrowUp', shiftKey: true })
    expect(onResize).toHaveBeenLastCalledWith(candidate(0, RANGE_START), 45)

    // The other option is untouched — there is no shared duration to reflow.
    expect(screen.getByTestId(`candidate-band-1-${RANGE_START}`)).toHaveTextContent('20:00–22:00')
  })
})

describe('CandidateGrid resize handle', () => {
  it('drags one option own length out of its handle_D211', () => {
    pinRects()
    const { onResize, props } = baseProps({
      candidates: [candidate(0, RANGE_START, 30), candidate(1, RANGE_START, 30)],
    })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const handle = band.querySelector('[aria-hidden="true"]') as HTMLElement
    fireEvent.pointerDown(handle, { button: 0, pointerType: 'mouse' })
    // One pixel per minute (see `pinRects`): y=90 is 21:30, so this option
    // becomes 90 minutes long — and only this one.
    fireEvent.pointerMove(window, { clientY: 90 })
    expect(band).toHaveTextContent('20:00–21:30')
    expect(screen.getByTestId(`candidate-band-1-${RANGE_START}`)).toHaveTextContent('20:00–20:30')

    fireEvent.pointerUp(window)

    expect(onResize).toHaveBeenCalledTimes(1)
    expect(onResize).toHaveBeenCalledWith(candidate(0, RANGE_START, 30), 90)
  })

  it('hands a resized length forward to the next tap_D211', () => {
    pinRects()
    const { onCreate, props } = baseProps({ candidates: [candidate(0, RANGE_START, 30)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const handle = band.querySelector('[aria-hidden="true"]') as HTMLElement
    fireEvent.pointerDown(handle, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerUp(window)

    fireEvent.click(screen.getByTestId(`candidate-slot-1-${RANGE_START}`))

    expect(onCreate).toHaveBeenCalledWith({ day: 1, start: RANGE_START, durationMinutes: 90 })
  })
})

describe('CandidateGrid touch gesture safety', () => {
  it('discards an in-flight band move when the browser cancels the gesture_P5TOUCH02', () => {
    pinRects()
    const { onMove, onRemove, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.pointerDown(band, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerMove(window, { clientY: 30 })
    fireEvent.pointerCancel(window)
    // A pointerup can still arrive after the cancel; it must find nothing.
    fireEvent.pointerUp(window)

    expect(onMove).not.toHaveBeenCalled()
    // Nor may the discarded move degenerate into a tap that removes the band.
    expect(onRemove).not.toHaveBeenCalled()
  })

  it('discards an in-flight duration drag when the browser cancels the gesture_P5TOUCH02', () => {
    pinRects()
    const { onResize, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const handle = band.querySelector('[aria-hidden="true"]') as HTMLElement
    fireEvent.pointerDown(handle, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerCancel(window)
    fireEvent.pointerUp(window)

    expect(onResize).not.toHaveBeenCalled()
  })

  it('discards an in-flight paint when the browser cancels the gesture_P5TOUCH02', () => {
    pinRects()
    const { onCreate, props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
    fireEvent.pointerDown(cell, { button: 0, pointerType: 'mouse', clientY: 0 })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerCancel(window)
    fireEvent.pointerUp(window)

    expect(onCreate).not.toHaveBeenCalled()
  })

  it('clears a pending band long press when the gesture is cancelled_P5TOUCH02', () => {
    vi.useFakeTimers()
    try {
      pinRects()
      const { onMove, onRemove, props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
      render(<CandidateGrid {...props} />)

      const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
      fireEvent.pointerDown(band, { button: 0, pointerType: 'touch' })
      fireEvent.pointerCancel(band)
      vi.advanceTimersByTime(400)
      // The cancelled press must not leave a tap behind either.
      fireEvent.pointerUp(band)

      expect(onMove).not.toHaveBeenCalled()
      expect(onRemove).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it('clears a pending paint long press when the gesture is cancelled_P5TOUCH02', () => {
    vi.useFakeTimers()
    try {
      pinRects()
      const { onCreate, props } = baseProps()
      render(<CandidateGrid {...props} />)

      const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
      fireEvent.pointerDown(cell, { button: 0, pointerType: 'touch', clientY: 0 })
      fireEvent.pointerCancel(cell)
      vi.advanceTimersByTime(400)
      fireEvent.pointerMove(window, { clientY: 90 })
      fireEvent.pointerUp(window)

      expect(onCreate).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it('vetoes the page scroll from a non-passive touchmove listener only while a press is live_P5TOUCH03', () => {
    vi.useFakeTimers()
    const addSpy = vi.spyOn(Element.prototype, 'addEventListener')
    const removeSpy = vi.spyOn(Element.prototype, 'removeEventListener')
    try {
      pinRects()
      const { props } = baseProps({ candidates: [candidate(0, RANGE_START)] })
      const view = render(<CandidateGrid {...props} />)

      // iOS locks touch-action at gesture start, so the class flip is a no-op
      // there: only a non-passive listener may refuse the pan.
      const index = addSpy.mock.calls.findIndex(
        (call) =>
          call[0] === 'touchmove' &&
          (call[2] as AddEventListenerOptions | undefined)?.passive === false,
      )
      expect(index).toBeGreaterThanOrEqual(0)
      const host = addSpy.mock.instances[index] as Element
      // The pointer target is the ARIA cell layer, so the host has to cover
      // both it and the painted columns.
      expect(host.querySelector('[data-daycol]')).not.toBeNull()
      expect(host.querySelector('[data-cell]')).not.toBeNull()

      const column = document.querySelector<HTMLElement>('[data-daycol="0"]')!
      const idle = new Event('touchmove', { bubbles: true, cancelable: true })
      column.dispatchEvent(idle)
      // Nothing is pressed: a finger drag scrolls, and a tap still places.
      expect(idle.defaultPrevented).toBe(false)

      const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
      fireEvent.pointerDown(band, { button: 0, pointerType: 'touch' })
      const pending = new Event('touchmove', { bubbles: true, cancelable: true })
      column.dispatchEvent(pending)
      expect(pending.defaultPrevented).toBe(true)

      // The press never armed, so the cancel lands on the band it started on.
      fireEvent.pointerCancel(band)
      const afterCancel = new Event('touchmove', { bubbles: true, cancelable: true })
      column.dispatchEvent(afterCancel)
      expect(afterCancel.defaultPrevented).toBe(false)

      // The empty-space paint press arms the same veto from the cell layer.
      const cell = screen.getByTestId(`candidate-slot-1-${RANGE_START}`)
      fireEvent.pointerDown(cell, { button: 0, pointerType: 'touch', clientY: 0 })
      const painting = new Event('touchmove', { bubbles: true, cancelable: true })
      cell.dispatchEvent(painting)
      expect(painting.defaultPrevented).toBe(true)
      fireEvent.pointerCancel(cell)

      view.unmount()
      expect(removeSpy.mock.calls.some((call) => call[0] === 'touchmove')).toBe(true)
    } finally {
      vi.useRealTimers()
      addSpy.mockRestore()
      removeSpy.mockRestore()
    }
  })
})

describe('CandidateGrid gridlines', () => {
  it('draws gridlines as elements on the label math, never as a repeating gradient_P5IOS01', () => {
    const { props } = baseProps()
    render(<CandidateGrid {...props} />)

    const column = document.querySelector<HTMLElement>('[data-daycol="0"]')!
    expect(column.style.backgroundImage).toBe('')

    const lines = column.querySelectorAll<HTMLElement>('[data-gridline]')
    // 20:00–22:00 at one line per 15-minute row, two of them on the hour.
    expect(lines).toHaveLength(8)
    expect(column.querySelectorAll('[data-gridline="hour"]')).toHaveLength(2)
    lines.forEach((line, index) => {
      expect(line.style.top).toBe(`${index * 15}px`)
    })

    const layer = lines[0].parentElement!
    expect(layer).toHaveAttribute('aria-hidden', 'true')
    expect(layer.className).toContain('pointer-events-none')
  })
})

describe('CandidateGrid friend and session layers', () => {
  const friends: Friend[] = [
    {
      id: 'friend-1',
      name: 'Mika',
      emoji: '🐙',
      imgUrl: null,
      masked: false,
      windows: [{ day: 0, start: RANGE_START, end: RANGE_START + 60, intent: 'free' }],
    },
  ]

  it('paints a friend availability chip at the window start', () => {
    const { props } = baseProps({ friends })
    render(<CandidateGrid {...props} />)

    expect(screen.getByText('Mika · 20:00–21:00')).toBeInTheDocument()
  })

  it('counts only free windows toward a cell name_F4', () => {
    const { props } = baseProps({
      friends: [
        ...friends,
        {
          id: 'friend-2',
          name: 'Sam',
          emoji: '🐧',
          imgUrl: null,
          masked: false,
          windows: [{ day: 0, start: RANGE_START, end: RANGE_START + 60, intent: 'maybe' }],
        },
      ],
    })
    render(<CandidateGrid {...props} />)

    expect(screen.getByTestId(`candidate-slot-0-${RANGE_START}`)).toHaveAttribute(
      'aria-label',
      'Mon 20:00 — 1 friend free',
    )
  })

  it('collapses more than three overlapping friend chips behind a +N badge', () => {
    const many: Friend[] = Array.from({ length: 5 }, (_, i) => ({
      id: `friend-${i}`,
      name: `Friend ${i}`,
      emoji: '🐙',
      imgUrl: null,
      masked: false,
      windows: [{ day: 0, start: RANGE_START, end: RANGE_START + 60, intent: 'free' as const }],
    }))
    const { props } = baseProps({ friends: many })
    render(<CandidateGrid {...props} />)

    expect(screen.getByText('+2')).toBeInTheDocument()
  })

  it('draws a masked friend as the anonymous disc, never inventing a glyph_D076', () => {
    const { props } = baseProps({
      friends: [
        {
          id: 'friend-9',
          name: 'Anonymous User',
          emoji: null,
          imgUrl: null,
          masked: true,
          windows: [{ day: 0, start: RANGE_START, end: RANGE_START + 60, intent: 'free' }],
        },
      ],
    })
    render(<CandidateGrid {...props} />)

    const chip = screen.getByText('Anonymous User · 20:00–21:00').parentElement as HTMLElement
    const disc = chip.querySelector('[aria-hidden="true"]') as HTMLElement
    expect(disc.className).toContain('bg-busy')
    expect(disc.textContent).toBe('●')
  })

  it('overlays scheduled sessions as their own pills', () => {
    const sessions: GameSession[] = [
      {
        id: 'session-1',
        title: 'Deep Rock night',
        day: 0,
        start: RANGE_START,
        end: RANGE_START + 60,
        occupancy: 3,
      },
    ]
    const { props } = baseProps({ sessions })
    render(<CandidateGrid {...props} />)

    const pill = screen.getByTestId('candidate-session-session-1')
    expect(pill).toHaveTextContent('Deep Rock night')
    expect(pill).toHaveTextContent('3 in')
  })
})
