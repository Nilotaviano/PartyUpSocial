import { Ban, CircleSlash, Clock, Radio, Trophy } from 'lucide-react'
import { cn } from '../../availability/lovable/utils'
import { AvatarDisc } from '../../friends/AvatarDisc'
import type { PollStatus, Voter } from './pollData'

/**
 * Ported from the Lovable prototype's `src/components/partyup/PollBits.tsx`
 * (StatusPill, VOTE_META, VoterAvatar, VoterRow), byte-faithful apart from:
 *  - `@/lib/utils` -> the already-ported `cn` in `../../availability/lovable/utils`.
 *  - Every user-facing string moves out to a `label`/`labels` prop — these are
 *    tiny presentational bits, so the caller resolves i18next strings once
 *    (matching how the calendar port kept `fmtRange`-style helpers pure) and
 *    passes the resolved text down, rather than each bit holding its own
 *    `useTranslation` hook.
 *  - The prototype's own hand-drawn disc (a `VOTERS[voterId]` lookup against
 *    a mock registry) is replaced by the shared `AvatarDisc` (seed/emoji/
 *    imgUrl/masked) fed from the real `Voter` view model in `./pollData`; the
 *    masked/anonymous voter therefore renders through the same D-076
 *    treatment as everywhere else, not a bespoke muted-circle style.
 *  - `VOTE_META` itself lives in `./pollData`, not here: this file exports
 *    only components, so `react-refresh/only-export-components` stays green.
 */

export function StatusPill({ status, label }: { status: PollStatus; label: string }) {
  const Icon =
    status === 'polling'
      ? Radio
      : status === 'closed'
        ? Clock
        : status === 'finalized'
          ? Trophy
          : status === 'canceled'
            ? Ban
            : CircleSlash
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center gap-1 rounded-full border px-2.5 py-1 font-display text-[11px] font-extrabold',
        status === 'polling' && 'border-free-foreground/40 bg-free text-free-foreground',
        status === 'closed' && 'border-maybe-foreground/40 bg-maybe text-maybe-foreground',
        status === 'finalized' && 'border-foreground/30 bg-accent text-accent-foreground',
        status === 'expired' && 'border-border bg-busy text-busy-foreground',
        status === 'canceled' && 'border-destructive/40 bg-destructive/15 text-destructive',
      )}
    >
      <Icon className="size-3.5" />
      {label}
    </span>
  )
}

/** Avatar disc. A masked voter renders the shared neutral `AvatarDisc` treatment. */
export function VoterAvatar({
  voter,
  size = 'xxs',
  ring,
}: {
  voter: Voter
  size?: 'xxs' | 'xs'
  ring?: string
}) {
  return (
    <span title={voter.name} className="inline-flex shrink-0">
      <AvatarDisc
        seed={voter.seed}
        emoji={voter.emoji}
        imgUrl={voter.imgUrl}
        masked={voter.masked}
        size={size}
        className={ring}
      />
      <span className="sr-only">{voter.name}</span>
    </span>
  )
}

export function VoterRow({
  voter,
  note,
  maskedSuffix,
}: {
  voter: Voter
  note?: string
  /** e.g. "(hidden)" — omitted for an unmasked voter regardless of value. */
  maskedSuffix?: string
}) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <VoterAvatar voter={voter} />
      <span className="min-w-0 truncate text-[13px] font-semibold">
        {voter.name}
        {voter.masked && maskedSuffix && (
          <span className="ml-1 text-[11px] font-normal text-muted-foreground">
            {maskedSuffix}
          </span>
        )}
      </span>
      {note && <span className="ml-auto shrink-0 text-[11px] text-muted-foreground">{note}</span>}
    </span>
  )
}
