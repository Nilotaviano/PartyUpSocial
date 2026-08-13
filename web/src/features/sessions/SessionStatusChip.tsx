import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { chipClasses } from '../../components/ui/classes'

/**
 * The session status hero chip (STYLING_OVERHAUL_DESIGN.md §3.4). Text, never
 * colour alone. An unrecognised wire status renders nothing rather than a chip
 * naming a state this build does not understand.
 *
 * The 300ms sticker-slap fires only on a transition the viewer actually
 * *observed* — Unconfirmed becoming Confirmed while the page is open. Arriving
 * on an already-Confirmed session is not a celebration, so the previous status
 * is seeded from the first render, not from an empty initial value.
 */
const known = new Set(['unconfirmed', 'confirmed', 'completed', 'expired', 'canceled'])

export function SessionStatusChip({ status }: { status: string }) {
  const { t } = useTranslation('sessions')
  const previous = useRef(status)
  const [celebrating, setCelebrating] = useState(false)

  useEffect(() => {
    const wasUnconfirmed = previous.current === 'unconfirmed'
    previous.current = status
    if (!wasUnconfirmed || status !== 'confirmed') return
    setCelebrating(true)
    const timer = window.setTimeout(() => setCelebrating(false), 300)
    return () => window.clearTimeout(timer)
  }, [status])

  if (!known.has(status)) return null

  const classes = [
    chipClasses,
    'motion-safe:transition-transform motion-safe:motion-celebrate motion-safe:ease-out',
    celebrating ? 'motion-safe:scale-110' : '',
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <span className={classes} data-testid="session-status-chip">
      {t(`status.${status}`)}
    </span>
  )
}
