import type { ComponentType } from 'react'
import {
  CalendarDays,
  Clock3,
  Crown,
  Dices,
  Gamepad2,
  Heart,
  Search,
  Sparkles,
  Trophy,
  X,
  XCircle,
} from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { Button } from '../../../components/ui/button'
import { fieldControlClasses } from '../../../components/ui/classes'
import { Tag } from '../../groups/GroupBits'
import { AvatarDisc } from '../../friends/AvatarDisc'
import type { SessionHistoryStats } from '../sessionQueries'
import type { HistoryEntryView, HistoryFilter, HistoryPlayerView } from './historyData'
import { emptyHistoryFilter, historyDurationLabel } from './historyData'

/**
 * Ported from the Lovable reference (`src/components/sessions/HistoryBits.tsx`)
 * onto the real API. Sanctioned deviations from the prototype:
 *  - the prototype's `person()` directory and its own `Avatar` are replaced by
 *    the server-resolved `MaskedParticipant` and the repo's `AvatarDisc`;
 *  - the prototype's cancellation reason line is dropped — no such domain
 *    field exists (D-221);
 *  - every string routes through `t()` (namespace `sessions`).
 */

function useSessionsTranslation() {
  return useTranslation('sessions')
}

function useZonedFormat() {
  const { i18n } = useSessionsTranslation()
  // Fake-local dates carry the viewer's wall clock in their UTC accessors (D-046).
  return (date: Date, options: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat(i18n.language, { ...options, timeZone: 'UTC' }).format(date)
}

/** Overlapping player discs. Decoration only — the count beside it carries the meaning. */
function PlayerStack({ players, max = 5 }: { players: HistoryPlayerView[]; max?: number }) {
  const shown = players.slice(0, max)
  const rest = players.length - shown.length

  return (
    <span aria-hidden="true" className="flex -space-x-2">
      {shown.map((player) => (
        <AvatarDisc
          key={player.key}
          className="ring-2 ring-card"
          emoji={player.emoji}
          imgUrl={player.imgUrl}
          masked={player.masked}
          seed={player.name ?? player.key}
          size="xs"
        />
      ))}
      {rest > 0 && (
        <span className="grid size-9 shrink-0 place-items-center rounded-full border-2 border-ink bg-muted font-display text-[11px] font-bold text-ink-soft ring-2 ring-card tabular-nums">
          +{rest}
        </span>
      )}
    </span>
  )
}

function RecapStat({
  icon: Icon,
  label,
  value,
  hint,
  className,
}: {
  icon: ComponentType<{ className?: string }>
  label: string
  value: string
  hint?: string
  className?: string
}) {
  return (
    <div
      className={['rounded-xl border-2 border-ink/25 bg-card px-3 py-2.5', className]
        .filter(Boolean)
        .join(' ')}
    >
      <dt className="flex items-center gap-1.5 font-display text-[11px] font-bold tracking-wide text-ink-soft uppercase">
        <Icon aria-hidden className="size-3.5 shrink-0" /> {label}
      </dt>
      <dd className="mt-0.5 truncate font-display text-[17px] font-extrabold text-ink">
        {value}
        {hint && (
          <span className="ml-1.5 align-middle text-[11px] font-bold text-ink-soft tabular-nums">
            {hint}
          </span>
        )}
      </dd>
    </div>
  )
}

/** D-222 recap over the viewer's whole history. Hidden entirely at zero completed sessions. */
export function HistoryRecap({ stats }: { stats: SessionHistoryStats }) {
  const { t } = useSessionsTranslation()
  const completedCount = Number(stats.completedCount)
  if (completedCount === 0) return null

  // A withheld identity keeps its place in the recap and renders through the
  // ordinary masked treatment (D-076) rather than being dropped.
  const mates = stats.topMates

  return (
    <section aria-labelledby="history-recap-heading" className="surface p-4">
      <h2
        className="flex items-center gap-2 font-display text-base font-extrabold text-ink"
        id="history-recap-heading"
      >
        <Sparkles aria-hidden className="size-4 shrink-0" />
        {t('history.recap.title')}
      </h2>

      <dl className="mt-3 grid gap-2 sm:grid-cols-2">
        <RecapStat
          icon={Dices}
          label={t('history.recap.sessionsPlayed')}
          value={String(completedCount)}
        />
        <RecapStat
          icon={Clock3}
          label={t('history.recap.hoursTogether')}
          value={t('history.recap.hoursValue', { count: Number(stats.hoursTogether) })}
        />
        {stats.topGame && (
          <RecapStat
            hint={t('history.recap.times', { count: Number(stats.topGame.times) })}
            icon={Trophy}
            label={t('history.recap.mostPlayed')}
            value={stats.topGame.name}
          />
        )}
        {mates.length > 0 && (
          <div className="rounded-xl border-2 border-ink/25 bg-card px-3 py-2.5">
            <dt className="flex items-center gap-1.5 font-display text-[11px] font-bold tracking-wide text-ink-soft uppercase">
              <Heart aria-hidden className="size-3.5 shrink-0" /> {t('history.recap.usualSuspects')}
            </dt>
            <dd className="mt-1.5 flex flex-wrap items-center gap-2">
              {mates.map((mate) => (
                <span className="flex min-w-0 items-center gap-1.5" key={String(mate.player.ordinal)}>
                  <AvatarDisc
                    emoji={mate.player.avatar?.emoji}
                    imgUrl={mate.player.avatar?.imgUrl}
                    masked={mate.player.displayName == null}
                    seed={mate.player.displayName ?? `masked-mate-${mate.player.ordinal}`}
                    size="xs"
                  />
                  <span className="truncate font-display text-[12.5px] font-bold text-ink">
                    {mate.player.displayName ?? t('detail.anonymous')}
                  </span>
                  <span className="text-[11px] text-ink-soft tabular-nums">
                    {t('history.recap.times', { count: Number(mate.times) })}
                  </span>
                </span>
              ))}
            </dd>
          </div>
        )}
      </dl>
    </section>
  )
}

function ChipRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="grid gap-1.5 sm:grid-cols-[90px_minmax(0,1fr)] sm:items-start sm:gap-3">
      <p className="pt-1 font-display text-[11px] font-bold tracking-wide text-ink-soft uppercase">
        {label}
      </p>
      <div className="flex flex-wrap gap-1.5">{children}</div>
    </div>
  )
}

function FilterChip({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      aria-pressed={active}
      className={[
        'inline-flex min-h-11 items-center gap-1.5 rounded-full border-2 px-3 font-display text-[12.5px] font-bold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
        active ? 'border-ink bg-primary text-primary-ink' : 'border-ink/25 bg-card text-ink',
      ].join(' ')}
      type="button"
      onClick={onClick}
    >
      {children}
    </button>
  )
}

/** D-223 narrowing: text search plus single-select game and played-with chips. */
export function HistoryFilters({
  filter,
  onChange,
  games,
  players,
  resultCount,
}: {
  filter: HistoryFilter
  onChange: (next: HistoryFilter) => void
  games: string[]
  players: string[]
  resultCount: number
}) {
  const { t } = useSessionsTranslation()
  const narrowed = filter.game != null || filter.player != null || filter.query.trim().length > 0

  return (
    <section aria-labelledby="history-filters-heading" className="surface p-3 sm:p-4">
      <h2 className="sr-only" id="history-filters-heading">
        {t('history.filters.heading')}
      </h2>
      <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center">
        <div className="relative min-w-0">
          <label className="sr-only" htmlFor="history-search">
            {t('history.filters.searchLabel')}
          </label>
          <Search
            aria-hidden
            className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-ink-soft"
          />
          <input
            className={`${fieldControlClasses} pl-9`}
            id="history-search"
            placeholder={t('history.filters.searchPlaceholder')}
            type="search"
            value={filter.query}
            onChange={(event) => onChange({ ...filter, query: event.target.value })}
          />
        </div>
        {narrowed ? (
          <Button variant="neutral" onClick={() => onChange(emptyHistoryFilter)}>
            <X aria-hidden className="size-4" />
            {t('history.filters.clear')}
          </Button>
        ) : (
          <p className="text-meta text-ink-soft tabular-nums">
            {t('history.filters.resultCount', { count: resultCount })}
          </p>
        )}
      </div>

      {(games.length > 0 || players.length > 0) && (
        <div className="mt-3 space-y-2">
          {games.length > 0 && (
            <ChipRow label={t('history.filters.game')}>
              {games.map((game) => (
                <FilterChip
                  active={filter.game === game}
                  key={game}
                  onClick={() =>
                    onChange({ ...filter, game: filter.game === game ? null : game })
                  }
                >
                  {game}
                </FilterChip>
              ))}
            </ChipRow>
          )}
          {players.length > 0 && (
            <ChipRow label={t('history.filters.playedWith')}>
              {players.map((player) => (
                <FilterChip
                  active={filter.player === player}
                  key={player}
                  onClick={() =>
                    onChange({ ...filter, player: filter.player === player ? null : player })
                  }
                >
                  {player}
                </FilterChip>
              ))}
            </ChipRow>
          )}
        </div>
      )}
    </section>
  )
}

/** One night in the record. Canceled entries are struck through and tagged (icon + text). */
export function HistoryEntry({ entry }: { entry: HistoryEntryView }) {
  const { t } = useSessionsTranslation()
  const format = useZonedFormat()

  return (
    <li>
      <Link
        className={[
          'block rounded-xl border-2 bg-card p-3 hover:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
          entry.canceled ? 'border-dashed border-ink/25' : 'border-ink/25',
        ].join(' ')}
        to={`/sessions/${entry.id}`}
      >
        <div className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
          <span
            aria-hidden
            className={[
              'grid size-11 shrink-0 place-items-center rounded-xl border-2 border-ink',
              entry.canceled ? 'bg-muted text-ink-soft' : 'bg-free text-free-ink',
            ].join(' ')}
          >
            {entry.canceled ? <XCircle className="size-5" /> : <Gamepad2 className="size-5" />}
          </span>

          <div className="min-w-0">
            <div className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-2">
              <h3
                className={[
                  'truncate font-display text-[15px] font-extrabold',
                  entry.canceled ? 'text-ink-soft line-through' : 'text-ink',
                ].join(' ')}
              >
                {entry.title}
              </h3>
              {entry.canceled ? (
                <Tag icon={XCircle}>{t('history.entry.canceled')}</Tag>
              ) : entry.organizedByViewer ? (
                <Tag icon={Crown} tone="accent">
                  {t('history.entry.organized')}
                </Tag>
              ) : null}
            </div>

            {entry.gameName != null && entry.gameName !== entry.title && (
              <p className="mt-0.5 truncate text-meta text-ink-soft">{entry.gameName}</p>
            )}

            <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-meta text-ink-soft tabular-nums">
              <span className="inline-flex items-center gap-1">
                <CalendarDays aria-hidden className="size-3.5 shrink-0" />
                {format(entry.startTime, { dateStyle: 'medium' })} ·{' '}
                {format(entry.startTime, { timeStyle: 'short' })}
              </span>
              {!entry.canceled && (
                <span className="inline-flex items-center gap-1">
                  <Clock3 aria-hidden className="size-3.5 shrink-0" />
                  {historyDurationLabel(entry.durationMinutes, t)}
                </span>
              )}
            </p>

            {!entry.canceled && entry.players.length > 0 && (
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <PlayerStack players={entry.players} />
                <span className="text-meta text-ink-soft">
                  {t('history.entry.played', { count: entry.players.length })}
                </span>
              </div>
            )}
          </div>
        </div>
      </Link>
    </li>
  )
}

/** Loading placeholder that mirrors the real rhythm: recap plus two month cards. */
export function HistorySkeleton() {
  const { t } = useSessionsTranslation()

  return (
    <div className="space-y-4" data-testid="history-skeleton">
      <div aria-hidden className="surface p-4">
        <div className="h-4 w-44 rounded bg-muted motion-safe:animate-pulse" />
        <div className="mt-3 grid gap-2 sm:grid-cols-2">
          {[0, 1, 2, 3].map((index) => (
            <div className="h-16 rounded-xl bg-muted motion-safe:animate-pulse" key={index} />
          ))}
        </div>
      </div>
      {[0, 1].map((month) => (
        <div aria-hidden className="surface p-4" key={month}>
          <div className="h-4 w-28 rounded bg-muted motion-safe:animate-pulse" />
          <div className="mt-3 space-y-2">
            {[0, 1, 2].map((index) => (
              <div className="h-[86px] rounded-xl bg-muted motion-safe:animate-pulse" key={index} />
            ))}
          </div>
        </div>
      ))}
      <p className="sr-only" role="status">
        {t('history.loading')}
      </p>
    </div>
  )
}
