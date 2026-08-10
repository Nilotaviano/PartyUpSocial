import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import type { AvailabilitySeriesView } from './availabilityQueries'

export type AvailabilityOneOff = {
  blockId: string
  startTime: string
  endTime: string
}

export type AvailabilitySummaryProps = {
  series: AvailabilitySeriesView[]
  oneOffs: AvailabilityOneOff[]
  timeZone: string
  onDeleteSeries: (seriesId: string) => void
  onDeleteBlock: (blockId: string) => void
  deletingId: string | null
}

const rowClasses =
  'flex flex-wrap items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-3 shadow-offset-card'

function endTime(startTime: string, durationMinutes: number): string {
  const [hours, minutes] = startTime.split(':').map(Number)
  const wrapped = (hours * 60 + minutes + durationMinutes) % (24 * 60)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(Math.floor(wrapped / 60))}:${pad(wrapped % 60)}`
}

/**
 * D-025: the viewer's own availability read as rules rather than grid marks,
 * so a weekly window can be removed as a whole. It renders `series` — the
 * definitions — never a grouping of the visible week's occurrences, so a rule
 * that expands to nothing this week is still listed and still deletable.
 */
export function AvailabilitySummary({
  series,
  oneOffs,
  timeZone,
  onDeleteSeries,
  onDeleteBlock,
  deletingId,
}: AvailabilitySummaryProps) {
  const { t, i18n } = useTranslation('availability')
  const [pendingSeries, setPendingSeries] = useState<string | null>(null)
  const deleteTrigger = useRef<HTMLElement | null>(null)

  // `dayOfWeek` is NodaTime's IsoDayOfWeek (Monday=1 … Sunday=7). 2026-08-02
  // is a Sunday, so adding it lands on the matching weekday.
  const weekdayName = (dayOfWeek: number) =>
    new Intl.DateTimeFormat(i18n.language, {
      weekday: 'short',
      timeZone: 'UTC',
    }).format(new Date(Date.UTC(2026, 7, 2 + dayOfWeek)))

  const oneOffLabel = (block: AvailabilityOneOff) => {
    const date = new Intl.DateTimeFormat(i18n.language, {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      timeZone,
    }).format(new Date(block.startTime))
    const time = (iso: string) =>
      new Intl.DateTimeFormat(i18n.language, {
        hour: '2-digit',
        minute: '2-digit',
        timeZone,
      }).format(new Date(iso))
    return t('summary.oneOffRule', {
      date,
      start: time(block.startTime),
      end: time(block.endTime),
    })
  }

  return (
    <section aria-labelledby="availability-summary-heading" className="space-y-3">
      <h2
        className="font-display text-h2 font-semibold text-ink"
        id="availability-summary-heading"
      >
        {t('summary.heading')}
      </h2>

      {series.length === 0 && oneOffs.length === 0 ? (
        <p className="text-ink-soft">{t('summary.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {series.map((rule) => {
            const label = t('summary.weeklyRule', {
              weekday: weekdayName(Number(rule.dayOfWeek)),
              start: rule.startTime,
              end: endTime(rule.startTime, Number(rule.durationMinutes)),
            })
            return (
              <li className={rowClasses} key={rule.seriesId}>
                <span className="text-ink">{label}</span>
                <Button
                  aria-label={t('summary.deleteNamed', { rule: label })}
                  disabled={deletingId === rule.seriesId}
                  type="button"
                  variant="danger"
                  onClick={(event) => {
                    deleteTrigger.current = event.currentTarget
                    setPendingSeries(rule.seriesId)
                  }}
                >
                  {t('summary.delete')}
                </Button>
              </li>
            )
          })}
          {oneOffs.map((block) => {
            const label = oneOffLabel(block)
            return (
              <li className={rowClasses} key={block.blockId}>
                <span className="text-ink">{label}</span>
                <Button
                  aria-label={t('summary.deleteNamed', { rule: label })}
                  disabled={deletingId === block.blockId}
                  type="button"
                  variant="danger"
                  onClick={() => onDeleteBlock(block.blockId)}
                >
                  {t('summary.delete')}
                </Button>
              </li>
            )
          })}
        </ul>
      )}

      <Dialog.Root
        open={pendingSeries !== null}
        onOpenChange={(open) => {
          if (!open) setPendingSeries(null)
        }}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 bg-scrim" />
          <Dialog.Content
            aria-describedby="delete-series-description"
            className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
            onCloseAutoFocus={(event) => {
              event.preventDefault()
              deleteTrigger.current?.focus()
            }}
          >
            <Dialog.Title className="font-display text-h2 font-semibold text-ink">
              {t('summary.deleteSeriesTitle')}
            </Dialog.Title>
            <Dialog.Description
              className="mt-2 text-ink-soft"
              id="delete-series-description"
            >
              {t('summary.deleteSeriesBody')}
            </Dialog.Description>
            <div className="mt-6 flex flex-wrap justify-end gap-2">
              <Button type="button" variant="neutral" onClick={() => setPendingSeries(null)}>
                {t('summary.cancel')}
              </Button>
              <Button
                type="button"
                variant="danger"
                onClick={() => {
                  const target = pendingSeries
                  setPendingSeries(null)
                  if (target) onDeleteSeries(target)
                }}
              >
                {t('summary.deleteSeriesConfirm')}
              </Button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </section>
  )
}
