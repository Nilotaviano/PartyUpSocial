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

  it('declares a poll options layer with no poll items to render_D111', () => {
    const onChange = vi.fn<(state: CalendarLayerState) => void>()
    render(<CalendarLayers friends={[]} onChange={onChange} />)

    const toggle = screen.getByLabelText('Poll options')
    expect(toggle).toBeChecked()
    expect(onChange.mock.calls.at(-1)![0].pollOptions).toBe(true)
    // The component accepts no poll-item prop at all — there is structurally
    // nothing it could render as a poll option (Phase 6 seam, A-080).
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
})
