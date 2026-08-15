import { cleanup, fireEvent, render, screen } from '@testing-library/react'
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

function baseProps(overrides: Partial<Parameters<typeof CandidateGrid>[0]> = {}) {
  const onToggle = vi.fn()
  const onMove = vi.fn()
  const onDurationChange = vi.fn()
  const props = {
    days: [0, 1],
    rangeStart: RANGE_START,
    rangeEnd: RANGE_END,
    step: 30,
    durationMin: 60,
    candidates: [] as Candidate[],
    onToggle,
    onMove,
    onDurationChange,
    max: 12,
    friends: [] as Friend[],
    sessions: [] as GameSession[],
    labels,
    ...overrides,
  }
  return { onToggle, onMove, onDurationChange, props }
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
  it('paints a candidate as one continuous band spanning the shared duration', () => {
    const { props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    // One band, not two 30-minute pills, and the range label sits at its start.
    expect(band).toHaveTextContent('20:00–21:00')
    expect(band.style.height).toBe('60px')
    expect(band.style.top).toBe('0px')
  })

  it('reflows every band when the duration field changes_D131', () => {
    const { props } = baseProps({
      candidates: [
        { day: 0, start: RANGE_START },
        { day: 1, start: RANGE_START + 30 },
      ],
    })
    const view = render(<CandidateGrid {...props} />)

    expect(screen.getByTestId(`candidate-band-0-${RANGE_START}`).style.height).toBe('60px')

    view.rerender(<CandidateGrid {...props} durationMin={90} />)

    const first = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const second = screen.getByTestId(`candidate-band-1-${RANGE_START + 30}`)
    expect(first.style.height).toBe('90px')
    expect(first).toHaveTextContent('20:00–21:30')
    expect(second.style.height).toBe('90px')
    expect(second).toHaveTextContent('20:30–22:00')
  })
})

describe('CandidateGrid placement and removal', () => {
  it('places a candidate at the tapped 30-minute cell', () => {
    const { onToggle, props } = baseProps()
    render(<CandidateGrid {...props} />)

    fireEvent.click(screen.getByTestId(`candidate-slot-1-${RANGE_START + 30}`))

    expect(onToggle).toHaveBeenCalledWith({ day: 1, start: RANGE_START + 30 })
  })

  it('removes the candidate a cell already sits under, never adding a duplicate_D080', () => {
    const { onToggle, props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    // The band spans 20:00–21:00, so the 20:30 cell beneath it toggles the
    // same candidate off rather than placing a second one.
    fireEvent.click(screen.getByTestId(`candidate-slot-0-${RANGE_START + 30}`))

    expect(onToggle).toHaveBeenCalledTimes(1)
    expect(onToggle).toHaveBeenCalledWith({ day: 0, start: RANGE_START })
  })

  it('removes a candidate when its band is tapped', () => {
    pinRects()
    const { onToggle, props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.pointerDown(band, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerUp(window)

    expect(onToggle).toHaveBeenCalledWith({ day: 0, start: RANGE_START })
  })

  it('stops placing once the option cap is reached, but still removes_D131', () => {
    const { onToggle, props } = baseProps({
      candidates: [{ day: 0, start: RANGE_START }],
      max: 1,
    })
    render(<CandidateGrid {...props} />)

    const free = screen.getByTestId(`candidate-slot-1-${RANGE_START}`)
    expect(free).toHaveAttribute('aria-disabled', 'true')
    fireEvent.click(free)
    expect(onToggle).not.toHaveBeenCalled()

    // The occupied cell stays live so a full grid can always be emptied again.
    fireEvent.click(screen.getByTestId(`candidate-slot-0-${RANGE_START}`))
    expect(onToggle).toHaveBeenCalledWith({ day: 0, start: RANGE_START })
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
    const { onToggle, props } = baseProps()
    render(<CandidateGrid {...props} />)

    const cell = screen.getByTestId(`candidate-slot-0-${RANGE_START}`)
    fireEvent.focus(cell)
    fireEvent.keyDown(cell, { key: 'Enter' })

    expect(onToggle).toHaveBeenCalledWith({ day: 0, start: RANGE_START })
  })

  it('moves a focused band with the arrow keys', () => {
    const { onMove, props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.keyDown(band, { key: 'ArrowDown' })
    expect(onMove).toHaveBeenCalledWith(
      { day: 0, start: RANGE_START },
      { day: 0, start: RANGE_START + 30 },
    )

    fireEvent.keyDown(band, { key: 'ArrowRight' })
    expect(onMove).toHaveBeenLastCalledWith(
      { day: 0, start: RANGE_START },
      { day: 1, start: RANGE_START },
    )
  })

  it('removes a focused band with Enter', () => {
    const { onToggle, props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    fireEvent.keyDown(screen.getByTestId(`candidate-band-0-${RANGE_START}`), { key: 'Enter' })
    expect(onToggle).toHaveBeenCalledWith({ day: 0, start: RANGE_START })
  })

  it('changes the shared duration with Shift and the arrow keys, in 15-minute steps_D131', () => {
    const { onDurationChange, props } = baseProps({
      candidates: [{ day: 0, start: RANGE_START }],
    })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.keyDown(band, { key: 'ArrowDown', shiftKey: true })
    expect(onDurationChange).toHaveBeenCalledWith(75)

    fireEvent.keyDown(band, { key: 'ArrowUp', shiftKey: true })
    expect(onDurationChange).toHaveBeenLastCalledWith(45)
  })
})

describe('CandidateGrid resize handle', () => {
  it('drags the shared duration out of the band handle and syncs it back', () => {
    pinRects()
    const { onDurationChange, props } = baseProps({
      candidates: [{ day: 0, start: RANGE_START }],
    })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const handle = band.querySelector('[aria-hidden="true"]') as HTMLElement
    fireEvent.pointerDown(handle, { button: 0, pointerType: 'mouse' })
    // One pixel per minute (see `pinRects`): y=90 is 21:30, so the shared
    // duration becomes 90 minutes for every option.
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerUp(window)

    expect(onDurationChange).toHaveBeenCalledWith(90)
  })
})

describe('CandidateGrid touch gesture safety', () => {
  it('discards an in-flight band move when the browser cancels the gesture_P5TOUCH02', () => {
    pinRects()
    const { onMove, onToggle, props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    fireEvent.pointerDown(band, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerMove(window, { clientY: 30 })
    fireEvent.pointerCancel(window)
    // A pointerup can still arrive after the cancel; it must find nothing.
    fireEvent.pointerUp(window)

    expect(onMove).not.toHaveBeenCalled()
    // Nor may the discarded move degenerate into a tap that removes the band.
    expect(onToggle).not.toHaveBeenCalled()
  })

  it('discards an in-flight duration drag when the browser cancels the gesture_P5TOUCH02', () => {
    pinRects()
    const { onDurationChange, props } = baseProps({
      candidates: [{ day: 0, start: RANGE_START }],
    })
    render(<CandidateGrid {...props} />)

    const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
    const handle = band.querySelector('[aria-hidden="true"]') as HTMLElement
    fireEvent.pointerDown(handle, { button: 0, pointerType: 'mouse' })
    fireEvent.pointerMove(window, { clientY: 90 })
    fireEvent.pointerCancel(window)
    fireEvent.pointerUp(window)

    expect(onDurationChange).not.toHaveBeenCalled()
  })

  it('clears a pending band long press when the gesture is cancelled_P5TOUCH02', () => {
    vi.useFakeTimers()
    try {
      pinRects()
      const { onMove, onToggle, props } = baseProps({
        candidates: [{ day: 0, start: RANGE_START }],
      })
      render(<CandidateGrid {...props} />)

      const band = screen.getByTestId(`candidate-band-0-${RANGE_START}`)
      fireEvent.pointerDown(band, { button: 0, pointerType: 'touch' })
      fireEvent.pointerCancel(band)
      vi.advanceTimersByTime(400)
      // The cancelled press must not leave a tap behind either.
      fireEvent.pointerUp(band)

      expect(onMove).not.toHaveBeenCalled()
      expect(onToggle).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it('vetoes the page scroll from a non-passive touchmove listener only while a band press is live_P5TOUCH03', () => {
    vi.useFakeTimers()
    const addSpy = vi.spyOn(Element.prototype, 'addEventListener')
    const removeSpy = vi.spyOn(Element.prototype, 'removeEventListener')
    try {
      pinRects()
      const { props } = baseProps({ candidates: [{ day: 0, start: RANGE_START }] })
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
      expect(host.querySelector('[data-daycol]')).not.toBeNull()

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
