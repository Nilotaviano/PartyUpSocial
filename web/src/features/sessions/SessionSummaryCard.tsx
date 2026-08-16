import { CalendarClock } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { cn } from '../availability/lovable/utils'
import { chipClasses } from '../../components/ui/classes'
import { OccupancyChip, OrganizerRow, StatusPill } from './lovable/SessionBits'
import {
  isKnownSessionStatus,
  mapSessionSummary,
  sessionPlatformLabel,
  type SessionSummaryResponse,
} from './lovable/sessionData'
import { isSessionInProgress } from './sessionQueries'

/**
 * The card markup `SessionListPage` originally hand-rolled, extracted so
 * Home's upcoming/discover regions and `DiscoverPage` render the same
 * game/platform/occupancy fields off the identical `SessionSummaryResponse`
 * (frontend audit 2026-08-15, finding 1.4) instead of a thinner title/status/
 * startTime-only card. `showInProgress` stays a caller choice — Home and
 * Discover's card lists don't carry `SessionListPage`'s upcoming-vs-history
 * `scope`, so the in-progress badge is opt-in rather than inferred here.
 *
 * `timeZone` defaults to the browser zone: callers that already fetch the
 * viewer profile (`SessionListPage`) pass its D-038/D-046 zone through;
 * callers that don't (Home, Discover) keep their existing browser-zone
 * rendering rather than this extraction silently adding a new profile fetch.
 */
export function SessionSummaryCard({
  session,
  timeZone,
  showInProgress = false,
  className,
}: {
  session: SessionSummaryResponse
  timeZone?: string
  showInProgress?: boolean
  className?: string
}) {
  const { t, i18n } = useTranslation('sessions')
  const zone = timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone
  const mapped = mapSessionSummary(session, zone, { anonymousOrganizer: t('detail.anonymous') })
  const inProgress = showInProgress && isSessionInProgress(session)
  const dimmed = mapped.status === 'canceled' || mapped.status === 'expired'

  const startText = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(mapped.startTime)

  const endText = new Intl.DateTimeFormat(i18n.language, {
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(new Date(mapped.startTime.getTime() + mapped.durationMinutes * 60_000))

  const occupancyLabel =
    mapped.maxCapacity == null
      ? t('list.occupancyUnlimited', { active: mapped.activeOccupancy })
      : t('list.occupancy', { active: mapped.activeOccupancy, max: mapped.maxCapacity })

  return (
    <li>
      <Link
        className={cn(
          'surface block h-full p-4 transition-colors hover:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
          dimmed && 'opacity-80',
          className,
        )}
        data-viewer-relation={session.viewerRelation}
        to={`/sessions/${session.id}`}
      >
        <div className="flex items-start justify-between gap-2">
          <h3 className="min-w-0 truncate font-display text-base font-extrabold text-ink">
            {mapped.title}
          </h3>
          {isKnownSessionStatus(mapped.status) && (
            <StatusPill status={mapped.status} label={t(`status.${mapped.status}`)} />
          )}
        </div>

        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="inline-flex items-center gap-2 rounded-xl border border-border bg-muted/50 px-2.5 py-2">
            <CalendarClock className="size-4 shrink-0" />
            <span className="min-w-0 truncate text-[12px] font-semibold tabular-nums">
              {startText}
            </span>
          </span>
          <span className="text-[12px] font-semibold text-muted-foreground tabular-nums">
            {t('detail.endsAt', { time: endText })}
          </span>
          {inProgress && <span className={chipClasses}>{t('list.inProgress')}</span>}
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
          {mapped.game && <span>{mapped.game.name}</span>}
          <span>{sessionPlatformLabel(mapped.platform, t)}</span>
        </div>

        <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
          <OrganizerRow organizer={mapped.organizer} organizerLabel={t('detail.organizer')} />
          <OccupancyChip label={occupancyLabel} />
        </div>
      </Link>
    </li>
  )
}
