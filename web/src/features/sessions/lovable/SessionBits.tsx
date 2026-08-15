import { Ban, CircleSlash, Clock, Radio, Trophy, Users } from 'lucide-react'
import { cn } from '../../availability/lovable/utils'
import { AvatarDisc } from '../../friends/AvatarDisc'
import type { OrganizerIdentity, SessionStatus } from './sessionData'

/**
 * `StatusPill` is a session-flavored pill (own icon set, own fill mapping)
 * rather than a shared generic component — the status vocabularies
 * (`PollStatus` / `SessionStatus`) don't line up one-to-one (a session has
 * no "polling" equivalent, a poll has no "completed"). The status
 * vocabulary itself — `unconfirmed` / `confirmed` / `completed` / `expired`
 * / `canceled` — and its i18n keys (`sessions.json#status`) are used by
 * `../SessionStatusChip.tsx` too.
 *
 * `OrganizerRow` fans out from `./sessionData`'s `OrganizerIdentity` so a
 * masked organizer renders through the same D-076 treatment as everywhere
 * else.
 */

export function StatusPill({ status, label }: { status: SessionStatus; label: string }) {
  const Icon =
    status === 'unconfirmed'
      ? Clock
      : status === 'confirmed'
        ? Radio
        : status === 'completed'
          ? Trophy
          : status === 'canceled'
            ? Ban
            : CircleSlash
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center gap-1 rounded-full border px-2.5 py-1 font-display text-[11px] font-extrabold',
        status === 'unconfirmed' && 'border-maybe-foreground/40 bg-maybe text-maybe-foreground',
        status === 'confirmed' && 'border-free-foreground/40 bg-free text-free-foreground',
        status === 'completed' && 'border-foreground/30 bg-accent text-accent-foreground',
        status === 'expired' && 'border-border bg-busy text-busy-foreground',
        status === 'canceled' && 'border-destructive/40 bg-destructive/15 text-destructive',
      )}
    >
      <Icon className="size-3.5" />
      {label}
    </span>
  )
}

export function OccupancyChip({ label }: { label: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full border-2 border-ink/25 bg-card px-2.5 py-1 text-[12px] font-semibold text-ink">
      <Users className="size-3.5 shrink-0" />
      {label}
    </span>
  )
}

export function OrganizerRow({
  organizer,
  organizerLabel,
}: {
  organizer: OrganizerIdentity
  /** e.g. "Organizer" — an sr-only prefix, never shown visually next to the name. */
  organizerLabel: string
}) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <span className="sr-only">{organizerLabel}</span>
      <AvatarDisc
        seed={organizer.seed}
        emoji={organizer.emoji}
        imgUrl={organizer.imgUrl}
        masked={organizer.masked}
        size="xxs"
      />
      <span className="min-w-0 truncate text-[12px] font-semibold text-ink-soft">
        {organizer.name}
      </span>
    </span>
  )
}
