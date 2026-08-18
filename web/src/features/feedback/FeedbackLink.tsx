import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { FeedbackDialog } from './FeedbackDialog'

/**
 * D-217's anonymous entry point: a discreet text link that opens the shared dialog, placed below
 * a guest page's own controls so it never competes with the RSVP or vote the page exists for.
 * It is a `<button>`, not an `<a>` — it opens a dialog rather than navigating.
 */
export function FeedbackLink({ className }: { className?: string }) {
  const { t } = useTranslation('feedback')
  const [open, setOpen] = useState(false)
  const trigger = useRef<HTMLButtonElement>(null)

  return (
    <>
      <button
        ref={trigger}
        className={[
          'cursor-pointer self-start rounded-control font-body text-meta text-ink-soft underline underline-offset-4 hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
          className,
        ]
          .filter(Boolean)
          .join(' ')}
        type="button"
        onClick={() => setOpen(true)}
      >
        {t('open')}
      </button>
      <FeedbackDialog open={open} triggerRef={trigger} onOpenChange={setOpen} />
    </>
  )
}
