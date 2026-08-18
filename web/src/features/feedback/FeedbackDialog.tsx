import { useMutation } from '@tanstack/react-query'
import { useId, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { fieldControlClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { useToast } from '../../components/ui/toastStore'
import {
  FEEDBACK_MESSAGE_LIMIT,
  FEEDBACK_TYPES,
  submitFeedback,
  type FeedbackContext,
  type FeedbackType,
} from './feedbackQueries'

type FeedbackDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Restored on close, so the entry point that opened the dialog keeps keyboard focus. */
  triggerRef?: React.RefObject<HTMLElement | null>
}

function chipClassesFor(selected: boolean) {
  return [
    'inline-flex min-h-11 cursor-pointer items-center rounded-full border-2 border-ink px-4 py-2 font-display text-sm font-bold',
    'has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-focus',
    selected ? 'bg-primary text-primary-ink' : 'bg-card text-ink',
  ].join(' ')
}

/**
 * D-217's dialog, reachable from the AccountMenu (signed-in) and from the guest RSVP/vote pages
 * (anonymous) — the two populations D-212 admits. It is a controlled dialog: the caller owns the
 * trigger and the open state, because the two entry points render nothing alike.
 *
 * The type control is a native radio group inside a fieldset, chip-skinned via `has-[:checked]`:
 * arrow-key roving, the checked state and the group label all come from the platform rather than
 * from hand-rolled `role="radio"` bookkeeping.
 *
 * Success closes the dialog, so the confirmation cannot live inside it — it is announced from a
 * `role="status"` region that outlives the panel, alongside the shell-owned toast.
 */
export function FeedbackDialog({ open, onOpenChange, triggerRef }: FeedbackDialogProps) {
  const { t, i18n } = useTranslation('feedback')
  const location = useLocation()
  const showToast = useToast()
  const fieldId = useId()
  const firstChip = useRef<HTMLInputElement>(null)

  const [type, setType] = useState<FeedbackType>('bug')
  const [message, setMessage] = useState('')
  const [emptyMessage, setEmptyMessage] = useState(false)
  const [confirmation, setConfirmation] = useState('')

  const context: FeedbackContext = {
    route: location.pathname + location.search,
    userAgent: typeof navigator === 'undefined' ? undefined : navigator.userAgent,
    locale: i18n.language,
    appVersion: __APP_VERSION__,
  }

  const submit = useMutation({
    mutationFn: () => submitFeedback(type, message.trim(), context),
    onSuccess: () => {
      setConfirmation(t('sent'))
      showToast({ message: t('sent') })
      reset()
      onOpenChange(false)
    },
  })

  function reset() {
    setType('bug')
    setMessage('')
    setEmptyMessage(false)
    submit.reset()
  }

  function handleOpenChange(next: boolean) {
    if (!next) reset()
    onOpenChange(next)
  }

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault()
    if (submit.isPending) return
    if (message.trim().length === 0) {
      setEmptyMessage(true)
      return
    }
    setEmptyMessage(false)
    setConfirmation('')
    submit.mutate()
  }

  const rateLimited = isApiError(submit.error) && submit.error.status === 429
  const errorText = emptyMessage
    ? t('errors.required')
    : submit.isError
      ? rateLimited
        ? t('errors.rateLimited')
        : t('errors.generic')
      : null

  return (
    <>
      {/*
        Lives outside the portal on purpose: it must still be in the tree — and still
        announced — after a successful submit has unmounted the panel.
      */}
      <p className="sr-only" role="status">
        {confirmation}
      </p>
      <Dialog.Root open={open} onOpenChange={handleOpenChange}>
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
            onOpenAutoFocus={(event) => {
              event.preventDefault()
              firstChip.current?.focus()
            }}
          >
            <Dialog.Title className="font-display text-lg font-semibold text-ink">
              {t('heading')}
            </Dialog.Title>
            <Dialog.Description
              className="mt-1 text-ink-soft"
              id={`${fieldId}-description`}
            >
              {t('description')}
            </Dialog.Description>

            {/*
              `noValidate`: the textarea keeps `required` for its SR semantics, but the UA's own
              validation bubble is unlocalized and would pre-empt the i18next message below.
            */}
            <form className="mt-4 flex flex-col gap-4" noValidate onSubmit={handleSubmit}>
              <fieldset className="flex flex-col gap-2">
                <legend className="font-body font-semibold text-ink">{t('type.legend')}</legend>
                <div className="flex flex-wrap gap-2">
                  {FEEDBACK_TYPES.map((value, index) => (
                    <label key={value} className={chipClassesFor(type === value)}>
                      <input
                        ref={index === 0 ? firstChip : undefined}
                        checked={type === value}
                        className="sr-only"
                        name={`${fieldId}-type`}
                        type="radio"
                        value={value}
                        onChange={() => setType(value)}
                      />
                      {t(`type.${value}`)}
                    </label>
                  ))}
                </div>
              </fieldset>

              <div className="flex flex-col gap-1.5">
                <label className="font-body font-semibold text-ink" htmlFor={`${fieldId}-message`}>
                  {t('message.label')}
                </label>
                <textarea
                  aria-describedby={`${fieldId}-counter`}
                  aria-invalid={errorText !== null || undefined}
                  className={fieldControlClasses}
                  id={`${fieldId}-message`}
                  maxLength={FEEDBACK_MESSAGE_LIMIT}
                  required
                  rows={6}
                  value={message}
                  onChange={(event) => {
                    setMessage(event.target.value)
                    if (event.target.value.trim().length > 0) setEmptyMessage(false)
                  }}
                />
                <p className="text-meta text-ink-soft" id={`${fieldId}-counter`}>
                  {t('message.counter', {
                    used: message.length,
                    limit: FEEDBACK_MESSAGE_LIMIT,
                  })}
                </p>
              </div>

              {errorText && (
                <p className="text-ink" role="alert">
                  {errorText}
                </p>
              )}

              <p className="text-meta text-ink-soft">{t('contextNotice')}</p>

              <div className="flex flex-wrap justify-end gap-2">
                <Dialog.Close asChild>
                  <Button type="button" variant="neutral">
                    {t('cancel')}
                  </Button>
                </Dialog.Close>
                <Button
                  aria-busy={submit.isPending}
                  disabled={submit.isPending}
                  type="submit"
                  variant="primary"
                >
                  {t('submit')}
                </Button>
              </div>
            </form>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  )
}
