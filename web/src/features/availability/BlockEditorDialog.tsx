import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'

export type BlockEditorValue = {
  start: string
  end: string
}

// Both fields are `datetime-local` strings ("YYYY-MM-DDTHH:mm", always
// zero-padded by the native control and by `datetimeLocalValue` in
// `WeekGridPage.tsx`) — chronological order is lexicographic order for that
// exact shape, so no `Date` parsing is needed to compare them.
function minutesOf(value: string): number {
  const time = value.split('T')[1]
  const minutes = time?.split(':')[1]
  return minutes ? Number(minutes) : 0
}

function isOffGrid(value: string): boolean {
  return minutesOf(value) % 15 !== 0
}

type BlockEditorDialogProps = {
  open: boolean
  initialStart: string
  initialEnd: string
  onOpenChange: (open: boolean) => void
  onSave: (value: BlockEditorValue) => void
  onDelete?: () => void
  triggerRef: React.RefObject<HTMLElement | null>
}

/**
 * The exact-time editor for an addressable occurrence (a `blockId` or a
 * `(seriesId, occurrenceDate)` skip, §5.1/§10.1) — the grid's non-drag,
 * non-hover path to change or remove a single window (§10.3).
 */
export function BlockEditorDialog({
  open,
  initialStart,
  initialEnd,
  onOpenChange,
  onSave,
  onDelete,
  triggerRef,
}: BlockEditorDialogProps) {
  const { t } = useTranslation('availability')
  const startRef = useRef<HTMLInputElement>(null)
  const [start, setStart] = useState(initialStart)
  const [end, setEnd] = useState(initialEnd)
  const [validationError, setValidationError] = useState<'endBeforeStart' | 'offGrid' | null>(
    null,
  )
  // Re-seeds the form from the newly-selected occurrence each time the
  // dialog freshly opens — adjusting state during render (not in an
  // effect, and not a ref — refs may not be read during render), the
  // pattern `SessionEditPage.tsx` already uses for the same "seed once per
  // open" need.
  const [seededOpen, setSeededOpen] = useState(false)
  if (open && !seededOpen) {
    setStart(initialStart)
    setEnd(initialEnd)
    setValidationError(null)
    setSeededOpen(true)
  } else if (!open && seededOpen) {
    setSeededOpen(false)
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            startRef.current?.focus()
          }}
        >
          <Dialog.Title className="font-display text-lg font-semibold text-ink">
            {t('block.editHeading')}
          </Dialog.Title>
          <form
            className="mt-4 space-y-4"
            onSubmit={(event) => {
              event.preventDefault()
              // Local-only: the server remains the authoritative validator
              // (`availability_validation`, §8) — this just saves a doomed
              // round trip and shows the reason before it happens.
              if (isOffGrid(start) || isOffGrid(end)) {
                setValidationError('offGrid')
                return
              }
              if (end <= start) {
                setValidationError('endBeforeStart')
                return
              }
              setValidationError(null)
              onSave({ start, end })
            }}
          >
            {validationError && (
              <p role="alert">{t(`validation.${validationError}`)}</p>
            )}
            <div>
              <label className="block font-medium text-ink" htmlFor="block-start">
                {t('block.start')}
              </label>
              <input
                ref={startRef}
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="block-start"
                step={900}
                type="datetime-local"
                value={start}
                onChange={(event) => setStart(event.target.value)}
              />
            </div>
            <div>
              <label className="block font-medium text-ink" htmlFor="block-end">
                {t('block.end')}
              </label>
              <input
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="block-end"
                step={900}
                type="datetime-local"
                value={end}
                onChange={(event) => setEnd(event.target.value)}
              />
            </div>
            <div className="flex flex-wrap justify-end gap-2">
              {onDelete && (
                <button
                  className={`px-4 py-2 text-danger-ink bg-danger dark:border-danger-ink ${raisedInteractiveClasses}`}
                  type="button"
                  onClick={onDelete}
                >
                  {t('block.delete')}
                </button>
              )}
              <Dialog.Close asChild>
                <button
                  className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                  type="button"
                >
                  {t('block.cancel')}
                </button>
              </Dialog.Close>
              <button
                className={`px-4 py-2 text-primary-ink bg-primary ${raisedInteractiveClasses}`}
                type="submit"
              >
                {t('block.save')}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
