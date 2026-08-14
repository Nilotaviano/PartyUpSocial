import { useTranslation } from 'react-i18next'

export type AgendaEntry = {
  id: string
  kind: 'availability' | 'session' | 'pollOption'
  start: Date
  end: Date
  title: string
}

type AgendaViewProps = {
  entries: AgendaEntry[]
  dateLabelFormatter: (date: Date) => string
  timeLabelFormatter: (date: Date) => string
}

/**
 * D-104: the chronological view of the identical range the week grid shows
 * — same `calendar.data`, sorted, not re-fetched (CalendarShell feeds both
 * from the one `GET /calendar` response).
 */
export function AgendaView({ entries, dateLabelFormatter, timeLabelFormatter }: AgendaViewProps) {
  const { t } = useTranslation('availability')
  const sorted = [...entries].sort((a, b) => a.start.getTime() - b.start.getTime())

  if (sorted.length === 0) {
    return <p className="text-meta text-ink-soft">{t('calendar.agendaEmpty')}</p>
  }

  return (
    <ul aria-label={t('a11y.agendaLabel')} className="flex flex-col">
      {sorted.map((entry) => (
        <li
          key={entry.id}
          className="flex min-h-11 flex-col justify-center gap-1 border-t border-ink/25 py-2 first:border-t-0 sm:flex-row sm:items-center sm:gap-4"
        >
          <span className="flex flex-wrap items-baseline gap-x-3 text-meta text-ink-soft sm:w-56 sm:shrink-0">
            <span>{dateLabelFormatter(entry.start)}</span>
            <span>
              {timeLabelFormatter(entry.start)}–{timeLabelFormatter(entry.end)}
            </span>
          </span>
          <span className="min-w-0 break-words font-medium text-ink">{entry.title}</span>
        </li>
      ))}
    </ul>
  )
}
