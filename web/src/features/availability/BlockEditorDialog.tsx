import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Dialog } from '../../components/ui/dialog'

export type BlockEditorValue = {
  start: string
  end: string
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
  // Re-seeds the form from the newly-selected occurrence each time the
  // dialog freshly opens — adjusting state during render (not in an
  // effect, and not a ref — refs may not be read during render), the
  // pattern `SessionEditPage.tsx` already uses for the same "seed once per
  // open" need.
  const [seededOpen, setSeededOpen] = useState(false)
  if (open && !seededOpen) {
    setStart(initialStart)
    setEnd(initialEnd)
    setSeededOpen(true)
  } else if (!open && seededOpen) {
    setSeededOpen(false)
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-slate-950/40" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            startRef.current?.focus()
          }}
        >
          <Dialog.Title className="text-lg font-semibold text-slate-950">
            {t('block.editHeading')}
          </Dialog.Title>
          <form
            className="mt-4 space-y-4"
            onSubmit={(event) => {
              event.preventDefault()
              onSave({ start, end })
            }}
          >
            <div>
              <label className="block font-medium text-slate-900" htmlFor="block-start">
                {t('block.start')}
              </label>
              <input
                ref={startRef}
                className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
                id="block-start"
                step={900}
                type="datetime-local"
                value={start}
                onChange={(event) => setStart(event.target.value)}
              />
            </div>
            <div>
              <label className="block font-medium text-slate-900" htmlFor="block-end">
                {t('block.end')}
              </label>
              <input
                className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
                  className="min-h-11 rounded-md border border-red-300 px-4 py-2 text-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700"
                  type="button"
                  onClick={onDelete}
                >
                  {t('block.delete')}
                </button>
              )}
              <Dialog.Close asChild>
                <button
                  className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                  type="button"
                >
                  {t('block.cancel')}
                </button>
              </Dialog.Close>
              <button
                className="min-h-11 rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
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
