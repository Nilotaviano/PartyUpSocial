import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import type { GridRange } from './WeekGrid'

export type CommitRecurrence = 'once' | 'weekly'

export type CommitBlockDialogProps = {
  range: GridRange
  /** Where the drag/tap/keyboard commit happened — positions the ≥sm
   * popover; ignored below `sm`, where the dialog is a bottom sheet
   * (CALENDAR_DESIGN.md §9 S3). */
  anchor: DOMRect
  pending: boolean
  onCancel: () => void
  onConfirm: (recurrence: CommitRecurrence) => void
}

const MOBILE_MEDIA_QUERY = '(max-width: 639px)'
const POPOVER_WIDTH_PX = 280
// The popover's real height depends on its content, which isn't known until
// after layout — this is a fixed upper-bound estimate (title + day line +
// segmented control + button row) used only to keep the clamp math a pure,
// synchronous function of the anchor and the viewport.
const POPOVER_HEIGHT_ESTIMATE_PX = 232
const VIEWPORT_MARGIN_PX = 16

/**
 * Centers the popover over the anchor, then slides it back inside the
 * viewport with a fixed margin on every edge — a release point near a corner
 * never pushes the card off-screen. Pure and unit-testable on purpose: it
 * takes the viewport size as a parameter instead of reading `window` itself.
 */
// eslint-disable-next-line react-refresh/only-export-components -- pure helper the test suite unit-tests directly; not a component
export function clampPopoverPosition(
  anchor: DOMRect,
  viewport: { width: number; height: number },
): { top: number; left: number } {
  const idealLeft = anchor.left + anchor.width / 2 - POPOVER_WIDTH_PX / 2
  const idealTop = anchor.top + anchor.height / 2 - POPOVER_HEIGHT_ESTIMATE_PX / 2
  const maxLeft = Math.max(VIEWPORT_MARGIN_PX, viewport.width - POPOVER_WIDTH_PX - VIEWPORT_MARGIN_PX)
  const maxTop = Math.max(VIEWPORT_MARGIN_PX, viewport.height - POPOVER_HEIGHT_ESTIMATE_PX - VIEWPORT_MARGIN_PX)
  return {
    left: Math.min(Math.max(idealLeft, VIEWPORT_MARGIN_PX), maxLeft),
    top: Math.min(Math.max(idealTop, VIEWPORT_MARGIN_PX), maxTop),
  }
}

function useIsMobileViewport(): boolean {
  const [isMobile, setIsMobile] = useState(() =>
    typeof matchMedia === 'undefined' ? false : matchMedia(MOBILE_MEDIA_QUERY).matches,
  )

  useEffect(() => {
    if (typeof matchMedia === 'undefined') return
    const media = matchMedia(MOBILE_MEDIA_QUERY)
    const listener = () => setIsMobile(media.matches)
    media.addEventListener('change', listener)
    return () => media.removeEventListener('change', listener)
  }, [])

  return isMobile
}

/**
 * D-009/§7: a painted range is a proposal until the viewer says how often it
 * repeats. `range` is fake-local (D-046), so every formatter here reads it
 * back through `timeZone: 'UTC'` — the viewer's own zone is already baked in.
 *
 * The caller mounts this only while a range is pending, which is what resets
 * the recurrence choice between two paints. §9 S3: on `≥sm` this renders as a
 * small popover anchored at the commit point; below `sm` it's a bottom sheet.
 * Both keep Radix's dialog semantics (focus trap, Escape, focus-return,
 * labelled by the range text) — only the positioning and skin differ.
 */
export function CommitBlockDialog({
  range,
  anchor,
  pending,
  onCancel,
  onConfirm,
}: CommitBlockDialogProps) {
  const { t, i18n } = useTranslation('availability')
  const [recurrence, setRecurrence] = useState<CommitRecurrence>('once')
  const isMobile = useIsMobileViewport()

  const time = (value: Date) =>
    new Intl.DateTimeFormat(i18n.language, {
      hour: '2-digit',
      minute: '2-digit',
      timeZone: 'UTC',
    }).format(value)
  const rangeText = t('commit.range', { start: time(range.start), end: time(range.end) })
  const dayText = new Intl.DateTimeFormat(i18n.language, {
    weekday: 'long',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  }).format(range.start)

  const recurrenceOptions: { value: CommitRecurrence; label: string }[] = [
    { value: 'once', label: t('commit.once') },
    { value: 'weekly', label: t('commit.weekly') },
  ]

  const body = (
    <>
      <Dialog.Title className="min-w-0 font-display text-h2 font-extrabold tabular-nums text-ink">
        {rangeText}
      </Dialog.Title>
      <Dialog.Description
        className="text-[12px] text-ink-soft"
        id="commit-block-description"
      >
        {dayText}
      </Dialog.Description>
      <fieldset className="mt-3">
        <legend className="sr-only">{t('commit.recurrenceLegend')}</legend>
        <div className="grid grid-cols-2 gap-1.5 rounded-control bg-busy p-1">
          {recurrenceOptions.map((option) => (
            <label
              key={option.value}
              className="flex h-11 cursor-pointer items-center justify-center rounded-control font-display text-[13px] font-bold text-ink-soft transition-colors has-[:checked]:bg-card has-[:checked]:text-ink has-[:checked]:shadow-[var(--shadow-card)] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-focus"
            >
              <input
                checked={recurrence === option.value}
                className="sr-only"
                name="recurrence"
                type="radio"
                value={option.value}
                onChange={() => setRecurrence(option.value)}
              />
              {option.label}
            </label>
          ))}
        </div>
      </fieldset>
      <div className="mt-3 flex gap-2">
        <Button
          className="flex-1"
          disabled={pending}
          type="button"
          variant="neutral"
          onClick={onCancel}
        >
          {t('commit.cancel')}
        </Button>
        <Button
          className="flex-1"
          disabled={pending}
          type="button"
          variant="primary"
          onClick={() => onConfirm(recurrence)}
        >
          {t('commit.save')}
        </Button>
      </div>
    </>
  )

  return (
    <Dialog.Root
      open
      onOpenChange={(next) => {
        if (!next) onCancel()
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        {isMobile ? (
          <Dialog.Content
            aria-describedby="commit-block-description"
            className="fixed inset-x-0 bottom-0 rounded-t-[1.5rem] border-t border-ink/25 bg-card p-4 pb-7 shadow-[var(--shadow-pop)] focus:outline-none"
          >
            <div aria-hidden className="mx-auto mb-3 h-1.5 w-10 rounded-full bg-ink/20" />
            {body}
          </Dialog.Content>
        ) : (
          <Dialog.Content
            aria-describedby="commit-block-description"
            className="fixed w-[280px] rounded-card border border-ink/25 bg-card p-3.5 shadow-[var(--shadow-pop)] focus:outline-none"
            style={clampPopoverPosition(anchor, {
              width: typeof window === 'undefined' ? 1024 : window.innerWidth,
              height: typeof window === 'undefined' ? 768 : window.innerHeight,
            })}
          >
            {body}
          </Dialog.Content>
        )}
      </Dialog.Portal>
    </Dialog.Root>
  )
}
