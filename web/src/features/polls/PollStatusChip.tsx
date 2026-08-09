import { useTranslation } from 'react-i18next'
import { isPollAwaitingSelection, type PollDetail, type PollSummary } from './pollQueries'

/**
 * Status text for a poll card/heading (design §5): "Polling" carries the
 * deadline as plain text (interpretation note 2 — no live region, no
 * ticking countdown), "closed-awaiting-selection" is presented from the
 * clock-inclusive gate (§5/§6.1), never from `status === 'closed'` alone.
 */
export function PollStatusChip({
  poll,
}: {
  poll: Pick<PollDetail | PollSummary, 'status' | 'deadline'>
}) {
  const { t, i18n } = useTranslation('polls')
  const deadlineText = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(poll.deadline))

  const awaitingSelection = isPollAwaitingSelection(poll)

  let label: string
  if (awaitingSelection) {
    label = t('status.awaitingSelection')
  } else if (poll.status === 'polling') {
    label = t('status.pollingUntil', { deadline: deadlineText })
  } else if (poll.status === 'finalized') {
    label = t('status.finalized')
  } else if (poll.status === 'expired') {
    label = t('status.expired')
  } else if (poll.status === 'canceled') {
    label = t('status.canceled')
  } else {
    label = t('status.closed')
  }

  return (
    <span className="rounded-full border-2 border-ink bg-accent px-2 py-0.5 font-display text-xs font-bold text-accent-ink dark:border-accent-ink">
      {label}
    </span>
  )
}
