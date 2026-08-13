import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { Section } from '../../components/ui/section'
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
  /** Lets two instances coexist in the DOM at once (CALENDAR_DESIGN.md §9 S4:
   * a `lg:hidden` mobile-disclosure copy alongside a `hidden lg:block`
   * always-visible one) without colliding on the default id below. */
  titleId?: string
}

const cardRowClasses =
  'flex items-center gap-2.5 rounded-card border border-ink/25 bg-card p-2.5'

const groupHeadingClasses =
  'font-display text-meta font-bold uppercase tracking-wide text-ink-soft'

function endTime(startTime: string, durationMinutes: number): string {
  const [hours, minutes] = startTime.split(':').map(Number)
  const wrapped = (hours * 60 + minutes + durationMinutes) % (24 * 60)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(Math.floor(wrapped / 60))}:${pad(wrapped % 60)}`
}

/** Decorative only (`aria-hidden`) — the row's own text already carries the
 * "repeats weekly" vs "this week only" distinction (WCAG 1.4.1). */
function RepeatGlyph() {
  return (
    <svg
      aria-hidden="true"
      className="size-3.5"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      viewBox="0 0 24 24"
    >
      <path d="M17 2l4 4-4 4" />
      <path d="M3 11V9a4 4 0 0 1 4-4h14" />
      <path d="M7 22l-4-4 4-4" />
      <path d="M21 13v2a4 4 0 0 1-4 4H3" />
    </svg>
  )
}

function CalendarGlyph() {
  return (
    <svg
      aria-hidden="true"
      className="size-3.5"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      viewBox="0 0 24 24"
    >
      <rect height="18" rx="2" width="18" x="3" y="4" />
      <path d="M16 2v4M8 2v4M3 10h18" />
    </svg>
  )
}

function KindChip({ kind }: { kind: 'recurring' | 'oneOff' }) {
  return (
    <span
      aria-hidden="true"
      className="grid size-7 shrink-0 place-items-center rounded-lg border border-ink/25 bg-free text-free-ink"
    >
      {kind === 'recurring' ? <RepeatGlyph /> : <CalendarGlyph />}
    </span>
  )
}

/**
 * D-025: the viewer's own availability read as rules rather than grid marks,
 * so a weekly window can be removed as a whole. It renders `series` — the
 * definitions — never a grouping of the visible week's occurrences, so a rule
 * that expands to nothing this week is still listed and still deletable.
 *
 * CALENDAR_DESIGN.md §9 S4: rules group under "Repeats weekly"/"This week
 * only" headers, each as a card row with a kind icon chip. Edit stays out of
 * this panel entirely (§9's explicitly-not-ported list) — the shipped block
 * editor / occurrence-scope flow, reached from the grid, is still the only
 * way to change a window's times; this panel only reads and deletes.
 */
export function AvailabilitySummary({
  series,
  oneOffs,
  timeZone,
  onDeleteSeries,
  onDeleteBlock,
  deletingId,
  titleId = 'availability-summary-heading',
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

  const isEmpty = series.length === 0 && oneOffs.length === 0

  return (
    <Section title={t('summary.heading')} titleId={titleId}>
      {isEmpty ? (
        <p className="text-ink-soft">{t('summary.empty')}</p>
      ) : (
        <div className="flex flex-col gap-4">
          {series.length > 0 && (
            <div className="flex flex-col gap-2">
              <p className={groupHeadingClasses}>{t('summary.weeklyHeading')}</p>
              <ul className="flex flex-col gap-2">
                {series.map((rule) => {
                  const label = t('summary.weeklyRule', {
                    weekday: weekdayName(Number(rule.dayOfWeek)),
                    start: rule.startTime,
                    end: endTime(rule.startTime, Number(rule.durationMinutes)),
                  })
                  return (
                    <li className={cardRowClasses} key={rule.seriesId}>
                      <KindChip kind="recurring" />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-ink">{label}</p>
                        <p className="truncate text-meta text-ink-soft">
                          {t('summary.kindRecurring')}
                        </p>
                      </div>
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
              </ul>
            </div>
          )}

          <div className="flex flex-col gap-2">
            <p className={groupHeadingClasses}>{t('summary.oneOffHeading')}</p>
            {oneOffs.length === 0 ? (
              <p className="text-ink-soft">{t('summary.oneOffEmpty')}</p>
            ) : (
              <ul className="flex flex-col gap-2">
                {oneOffs.map((block) => {
                  const label = oneOffLabel(block)
                  return (
                    <li className={cardRowClasses} key={block.blockId}>
                      <KindChip kind="oneOff" />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-ink">{label}</p>
                        <p className="truncate text-meta text-ink-soft">
                          {t('summary.kindOneOff')}
                        </p>
                      </div>
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
          </div>
        </div>
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
    </Section>
  )
}
