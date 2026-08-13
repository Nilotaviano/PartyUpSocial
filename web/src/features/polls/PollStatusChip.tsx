import { useTranslation } from 'react-i18next'
import { chipClasses } from '../../components/ui/classes'
import { pollWindowState, type PollWindow } from './pollQueries'

/**
 * Status text for a poll card/heading (design §5): "Polling" carries the
 * deadline as plain text (interpretation note 2 — no live region, no
 * ticking countdown); "closed-awaiting-selection" and the past-the-window
 * state are both presented from `pollWindowState`'s clock-inclusive gate
 * (§5/§6.1), never from `status === 'closed'` alone — terminal statuses
 * (finalized/expired/canceled) render directly from `status`.
 */
export function PollStatusChip({ poll }: { poll: PollWindow }) {
  const { t, i18n } = useTranslation('polls')
  const deadlineText = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(poll.deadline))

  let label: string
  if (poll.status === 'finalized') {
    label = t('status.finalized')
  } else if (poll.status === 'expired') {
    label = t('status.expired')
  } else if (poll.status === 'canceled') {
    label = t('status.canceled')
  } else {
    const windowState = pollWindowState(poll)
    if (windowState === 'expiredPending') {
      label = t('status.expiredPending')
    } else if (windowState === 'awaitingSelection') {
      label = t('status.awaitingSelection')
    } else if (windowState === 'polling') {
      label = t('status.pollingUntil', { deadline: deadlineText })
    } else {
      label = t('status.closed')
    }
  }

  return (
    <span className={chipClasses}>{label}</span>
  )
}
