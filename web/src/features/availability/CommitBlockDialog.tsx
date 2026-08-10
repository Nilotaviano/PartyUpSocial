import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import type { GridRange } from './WeekGrid'

export type CommitRecurrence = 'once' | 'weekly'

export type CommitBlockDialogProps = {
  range: GridRange
  pending: boolean
  onCancel: () => void
  onConfirm: (recurrence: CommitRecurrence) => void
}

/**
 * D-009/§7: a painted range is a proposal until the viewer says how often it
 * repeats. `range` is fake-local (D-046), so every formatter here reads it
 * back through `timeZone: 'UTC'` — the viewer's own zone is already baked in.
 *
 * The caller mounts this only while a range is pending, which is what resets
 * the recurrence choice between two paints.
 */
export function CommitBlockDialog({
  range,
  pending,
  onCancel,
  onConfirm,
}: CommitBlockDialogProps) {
  const { t, i18n } = useTranslation('availability')
  const [recurrence, setRecurrence] = useState<CommitRecurrence>('once')

  const date = new Intl.DateTimeFormat(i18n.language, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  }).format(range.start)
  const time = (value: Date) =>
    new Intl.DateTimeFormat(i18n.language, {
      hour: '2-digit',
      minute: '2-digit',
      timeZone: 'UTC',
    }).format(value)
  const label = t('summary.oneOffRule', {
    date,
    start: time(range.start),
    end: time(range.end),
  })

  return (
    <Dialog.Root
      open
      onOpenChange={(next) => {
        if (!next) onCancel()
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="commit-block-description"
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
        >
          <Dialog.Title className="font-display text-h2 font-semibold text-ink">
            {t('commit.title')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="commit-block-description">
            {label}
          </Dialog.Description>
          <fieldset className="mt-4">
            <legend className="font-body font-semibold text-ink">
              {t('commit.recurrenceLegend')}
            </legend>
            <label className="mt-1 flex min-h-11 items-center gap-2 text-ink">
              <input
                checked={recurrence === 'once'}
                name="recurrence"
                type="radio"
                value="once"
                onChange={() => setRecurrence('once')}
              />
              {t('commit.once')}
            </label>
            <label className="flex min-h-11 items-center gap-2 text-ink">
              <input
                checked={recurrence === 'weekly'}
                name="recurrence"
                type="radio"
                value="weekly"
                onChange={() => setRecurrence('weekly')}
              />
              {t('commit.weekly')}
            </label>
          </fieldset>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Button disabled={pending} type="button" variant="neutral" onClick={onCancel}>
              {t('commit.discard')}
            </Button>
            <Button
              disabled={pending}
              type="button"
              variant="primary"
              onClick={() => onConfirm(recurrence)}
            >
              {t('commit.save')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
