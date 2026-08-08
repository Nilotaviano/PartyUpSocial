import { useTranslation } from 'react-i18next'

export type AgendaEntry = {
  id: string
  kind: 'availability' | 'session'
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
 * — same `calendar.data`, sorted, not re-fetched (WeekGridPage feeds both
 * from the one `GET /calendar` response).
 */
export function AgendaView({ entries, dateLabelFormatter, timeLabelFormatter }: AgendaViewProps) {
  const { t } = useTranslation('availability')
  const sorted = [...entries].sort((a, b) => a.start.getTime() - b.start.getTime())

  if (sorted.length === 0) {
    return <p>{t('calendar.agendaEmpty')}</p>
  }

  return (
    <ul aria-label={t('a11y.agendaLabel')} className="divide-y divide-ink/25">
      {sorted.map((entry) => (
        <li key={entry.id} className="flex min-h-11 items-center gap-4 py-2">
          <span className="w-28 text-sm text-ink-soft">{dateLabelFormatter(entry.start)}</span>
          <span className="w-32 text-sm text-ink-soft">
            {timeLabelFormatter(entry.start)}–{timeLabelFormatter(entry.end)}
          </span>
          <span className="font-medium text-ink">{entry.title}</span>
        </li>
      ))}
    </ul>
  )
}
