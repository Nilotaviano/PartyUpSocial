import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Dialog } from '../../components/ui/dialog'

export type OccurrenceScope = 'onlyThis' | 'thisAndFuture'

type OccurrenceScopeDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: (scope: OccurrenceScope) => void
  triggerRef: React.RefObject<HTMLElement | null>
}

/**
 * D-134/D-143: editing or deleting one occurrence of a recurring series asks
 * which occurrences are affected, defaulting to "only this occurrence" —
 * every control is a native, keyboard-reachable radio/button, so the choice
 * needs no pointer (§10.3, P5-CAL-01).
 */
export function OccurrenceScopeDialog({
  open,
  onOpenChange,
  onConfirm,
  triggerRef,
}: OccurrenceScopeDialogProps) {
  const { t } = useTranslation('availability')
  const [scope, setScope] = useState<OccurrenceScope>('onlyThis')
  const firstRadio = useRef<HTMLInputElement>(null)

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) setScope('onlyThis')
        onOpenChange(next)
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-ink/40" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            firstRadio.current?.focus()
          }}
        >
          <Dialog.Title className="font-display text-lg font-semibold text-ink">
            {t('block.scopeHeading')}
          </Dialog.Title>
          <fieldset className="mt-4 space-y-2">
            <legend className="sr-only">{t('block.scopeHeading')}</legend>
            <label className="flex min-h-11 items-center gap-2">
              <input
                ref={firstRadio}
                checked={scope === 'onlyThis'}
                name="occurrence-scope"
                type="radio"
                value="onlyThis"
                onChange={() => setScope('onlyThis')}
              />
              {t('block.scopeOnlyThis')}
            </label>
            <label className="flex min-h-11 items-center gap-2">
              <input
                checked={scope === 'thisAndFuture'}
                name="occurrence-scope"
                type="radio"
                value="thisAndFuture"
                onChange={() => setScope('thisAndFuture')}
              />
              {t('block.scopeThisAndFuture')}
            </label>
          </fieldset>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                className="min-h-11 rounded-control border-2 border-ink bg-card px-4 py-2 font-display font-semibold text-ink shadow-offset-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed"
                type="button"
              >
                {t('block.cancel')}
              </button>
            </Dialog.Close>
            <button
              className="min-h-11 rounded-control border-2 border-ink bg-primary px-4 py-2 font-display font-semibold text-primary-ink shadow-offset-control focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed"
              type="button"
              onClick={() => onConfirm(scope)}
            >
              {t('block.confirm')}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
