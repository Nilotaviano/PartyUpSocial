import { useId } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'

type IosInstallDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Restored on close, so the entry point that opened the dialog keeps keyboard focus. */
  triggerRef?: React.RefObject<HTMLElement | null>
}

/**
 * D-220's iOS arm: Safari fires no `beforeinstallprompt`, so the "install" action shows the
 * share-sheet recipe instead. Controlled by the caller for the same reason `FeedbackDialog` is —
 * the banner and the AccountMenu row render nothing alike but open the same panel.
 */
export function IosInstallDialog({ open, onOpenChange, triggerRef }: IosInstallDialogProps) {
  const { t } = useTranslation('pwa')
  const fieldId = useId()

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby={`${fieldId}-description`}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            if (!triggerRef) return
            event.preventDefault()
            triggerRef.current?.focus()
          }}
        >
          <Dialog.Title className="font-display text-lg font-semibold text-ink">
            {t('install.ios.heading')}
          </Dialog.Title>
          <Dialog.Description className="mt-1 text-ink-soft" id={`${fieldId}-description`}>
            {t('install.ios.description')}
          </Dialog.Description>

          <ol className="mt-4 list-decimal space-y-2 pl-5 text-ink">
            <li>{t('install.ios.step1')}</li>
            <li>{t('install.ios.step2')}</li>
            <li>{t('install.ios.step3')}</li>
          </ol>

          <div className="mt-6 flex justify-end">
            <Dialog.Close asChild>
              <Button type="button" variant="primary">
                {t('install.ios.close')}
              </Button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
