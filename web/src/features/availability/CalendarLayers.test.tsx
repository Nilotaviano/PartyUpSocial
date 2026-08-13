import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../i18n'
import { CalendarLayers, type CalendarLayerState, type MaskedParticipant } from './CalendarLayers'

function friend(ordinal: number, displayName: string | null = `Friend ${ordinal}`): MaskedParticipant {
  return { ordinal, displayName }
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('CalendarLayers', () => {
  it('shows every friend by default and filters reduce it_D117', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    const friends = [friend(0, 'Ana'), friend(1, 'Bruno'), friend(2, null)]
    render(<CalendarLayers friends={friends} onChange={onChange} />)

    const last = () => onChange.mock.calls.at(-1)![0]
    expect(last().friendAvailability).toBe(true)
    expect(last().shownFriendOrdinals).toEqual(new Set([0, 1, 2]))
    expect(screen.getByLabelText('Ana')).toBeChecked()
    expect(screen.getByLabelText('Bruno')).toBeChecked()
    expect(screen.getByLabelText('Anonymous User')).toBeChecked()

    fireEvent.click(screen.getByLabelText('Bruno'))

    expect(last().shownFriendOrdinals).toEqual(new Set([0, 2]))
    expect(screen.getByLabelText('Bruno')).not.toBeChecked()
    expect(screen.getByLabelText('Ana')).toBeChecked()
  })

  it('toggling the friend-availability layer off hides the per-friend filter', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    render(<CalendarLayers friends={[friend(0, 'Ana')]} onChange={onChange} />)

    expect(screen.getByLabelText('Ana')).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText("Friends' availability"))

    expect(onChange.mock.calls.at(-1)![0].friendAvailability).toBe(false)
    expect(screen.queryByLabelText('Ana')).not.toBeInTheDocument()
  })

  it('renders each in-range poll as a legend item under the poll options toggle_D111', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    render(
      <CalendarLayers
        friends={[]}
        pollItems={[
          { pollId: 'poll-1', title: 'Raid night poll' },
          { pollId: 'poll-2', title: 'Board game poll' },
        ]}
        onChange={onChange}
      />,
    )

    const toggle = screen.getByLabelText('Poll options')
    expect(toggle).toBeChecked()
    expect(onChange.mock.calls.at(-1)![0].pollOptions).toBe(true)

    const items = screen.getAllByRole('listitem')
    expect(items).toHaveLength(2)
    expect(items[0]).toHaveTextContent('Raid night poll')
    expect(items[1]).toHaveTextContent('Board game poll')

    // Toggling the layer off hides the poll legend items too — one toggle, one data source.
    fireEvent.click(toggle)
    expect(screen.queryByRole('listitem')).not.toBeInTheDocument()
  })

  it('renders no poll legend items with an empty poll list, the Phase 6 seam default', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    render(<CalendarLayers friends={[]} onChange={onChange} />)

    expect(screen.getByLabelText('Poll options')).toBeChecked()
    expect(screen.queryByRole('listitem')).not.toBeInTheDocument()
  })

  it('resets filters on remount with no persisted write_D135', () => {
    const localStorageSpy = vi.spyOn(Storage.prototype, 'setItem')
    const historySpy = vi.spyOn(window.history, 'pushState')
    const onChangeFirst = vi.fn<(state: CalendarLayerState) => void>()
    const first = render(
      <CalendarLayers friends={[friend(0, 'Ana')]} onChange={onChangeFirst} />,
    )

    fireEvent.click(screen.getByLabelText('Ana'))
    expect(onChangeFirst.mock.calls.at(-1)![0].shownFriendOrdinals).toEqual(new Set())
    first.unmount()

    const onChangeSecond = vi.fn<(state: CalendarLayerState) => void>()
    render(<CalendarLayers friends={[friend(0, 'Ana')]} onChange={onChangeSecond} />)

    // A fresh mount starts from the D-117 default again — component state,
    // never a server call, `localStorage`, or a URL default.
    expect(onChangeSecond.mock.calls.at(-1)![0].shownFriendOrdinals).toEqual(new Set([0]))
    expect(screen.getByLabelText('Ana')).toBeChecked()
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(historySpy).not.toHaveBeenCalled()
  })
  // PHASE8_DESIGN.md §3 S7 (D-139, WCAG 1.4.1): the legend pairs each layer with a colour swatch,
  // which is exactly the shape that fails "use of colour" if the swatch is the only thing
  // distinguishing one layer from another. It does not here — every swatch is decorative
  // (`aria-hidden`) and the meaning is carried by the control's own text label — but nothing
  // asserted that, so a future restyle could quietly drop the text and leave colour alone.
  it('codes every calendar layer by text, never by its colour swatch alone_D139', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    const { container } = render(
      <CalendarLayers friends={[friend(0, 'Ana')]} onChange={onChange} />,
    )

    const layerNames = [
      'My availability',
      "Friends' availability",
      'Sessions',
      'Poll options',
    ]
    for (const name of layerNames) {
      // A text-derived accessible name is the whole point: getByLabelText resolves it from the
      // label's own text content, which is what a screen reader announces.
      expect(screen.getByLabelText(name)).toHaveAttribute('type', 'checkbox')
    }

    // Every swatch is decorative and none of them carries text, so removing colour entirely
    // leaves the legend fully readable.
    const swatches = container.querySelectorAll('span.rounded-full')
    expect(swatches.length).toBe(layerNames.length)
    for (const swatch of swatches) {
      expect(swatch).toHaveAttribute('aria-hidden', 'true')
      expect(swatch.textContent).toBe('')
    }
  })

  // CALENDAR_DESIGN.md §9 S4's verified S2 finding: the legend's swatches had
  // drifted from what `WeekGrid.tsx` actually paints for the sessions/friend/
  // poll-option layers — this pins each swatch to the grid's real fill/border
  // so a future grid restyle without a matching legend update fails loudly.
  it('paints every swatch with the fill/border the grid itself uses for that layer_S4', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    const { container } = render(
      <CalendarLayers friends={[friend(0, 'Ana')]} onChange={onChange} />,
    )
    const swatches = Array.from(container.querySelectorAll('span[aria-hidden="true"].rounded-full'))
    const classesFor = (index: number) => swatches[index]!.className

    // My availability: the grid's free-cell fill, never `bg-free-ink`.
    expect(classesFor(0)).toMatch(/\bbg-free\b/)
    // Friends: a neutral busy-fill disc with an ink ring, never `bg-ink`.
    expect(classesFor(1)).toMatch(/\bbg-busy\b/)
    expect(classesFor(1)).not.toMatch(/\bbg-ink\b/)
    // Sessions: an accent-filled pill with a 2px ink-tinted border, never
    // `bg-free-ink`.
    expect(classesFor(2)).toMatch(/\bbg-accent\b/)
    expect(classesFor(2)).toMatch(/\bborder-2\b/)
    expect(classesFor(2)).not.toMatch(/\bbg-free-ink\b/)
    // Poll options: a dashed-border card-fill pill, never `bg-maybe`.
    expect(classesFor(3)).toMatch(/\bbg-card\b/)
    expect(classesFor(3)).toMatch(/\bborder-dashed\b/)
    expect(classesFor(3)).not.toMatch(/\bbg-maybe\b/)
  })

  // CALENDAR_DESIGN.md §9 S4: the legend restyles as a card, fieldset/legend
  // semantics unchanged.
  it('restyles as a surfaced legend card without changing fieldset semantics_S4', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    const { container } = render(<CalendarLayers friends={[]} onChange={onChange} />)

    const fieldset = container.querySelector('fieldset')
    expect(fieldset).toHaveClass('surface')
    expect(fieldset?.querySelector('legend')).toHaveTextContent('Layers')
  })
})
