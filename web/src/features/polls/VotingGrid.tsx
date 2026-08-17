import { useTranslation } from 'react-i18next'
import type { PollOption } from './pollQueries'

export type VoteChoiceValue = 'available' | 'maybe' | 'unavailable'

const CHOICES: VoteChoiceValue[] = ['available', 'maybe', 'unavailable']

// design §5 (verbatim, owner-ratified): value glyphs always render, aria-hidden — the
// accessible name carries the value. Available = free pair + check, Maybe = maybe pair +
// question mark, Unavailable = busy pair + cross.
const glyph: Record<VoteChoiceValue, string> = {
  available: '✓',
  maybe: '?',
  unavailable: '✕',
}

// unselected = card-filled, glyph in ink-soft; selected = value fill + 2px inset ink ring
// (Phase 5.1 selection idiom — fills alone read <3:1 vs card and cannot carry checkedness).
const fillClasses: Record<VoteChoiceValue, string> = {
  available: 'aria-checked:bg-free aria-checked:text-free-ink',
  maybe: 'aria-checked:bg-maybe aria-checked:text-maybe-ink',
  unavailable: 'aria-checked:bg-busy aria-checked:text-busy-ink',
}

function optionTallyText(
  option: PollOption,
  t: (key: string, options?: Record<string, unknown>) => string,
) {
  const parts: string[] = []
  const available = Number(option.availableCount)
  const maybe = Number(option.maybeCount)
  const unavailable = Number(option.unavailableCount)
  if (available > 0) parts.push(t('detail.tallyAvailable', { count: available }))
  if (maybe > 0) parts.push(t('detail.tallyMaybe', { count: maybe }))
  if (unavailable > 0) parts.push(t('detail.tallyUnavailable', { count: unavailable }))
  return parts.length > 0 ? parts.join(' · ') : t('detail.tallyNone')
}

type VotingGridProps = {
  options: PollOption[]
  disabled: boolean
  pendingOptionId: string | null
  locale: string
  onVote: (optionId: string, choice: VoteChoiceValue) => void
}

/**
 * design §5 (the phase's second a11y hotspot, binding verbatim): options render as rows, each
 * row an independent radiogroup of three vote controls — this page is NOT an ARIA grid.
 * Roving tabindex + arrow keys mirror the shipped ThemeToggle APG idiom. Control accessible
 * name is "{option time}, {value}"; the per-option tally renders as plain text alongside.
 */
export function VotingGrid({
  options,
  disabled,
  pendingOptionId,
  locale,
  onVote,
}: VotingGridProps) {
  const { t } = useTranslation('polls')

  function onKeyDown(
    event: React.KeyboardEvent<HTMLButtonElement>,
    optionId: string,
    current: VoteChoiceValue | null,
  ) {
    const delta =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0
    if (delta === 0) return
    event.preventDefault()
    const from = current ? CHOICES.indexOf(current) : 0
    const next = CHOICES[(from + delta + CHOICES.length) % CHOICES.length]
    if (!disabled) onVote(optionId, next)
    const radios =
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>(
        '[role=radio]',
      )
    radios?.[CHOICES.indexOf(next)]?.focus()
  }

  return (
    <ul className="mt-2 space-y-2">
      {options.map((option) => {
        // D-211: every option owns its own end, so the row states the whole
        // window — a bare start would read as if they all shared one length.
        const start = new Date(option.startTime)
        const end = new Date(start.getTime() + Number(option.durationMinutes) * 60_000)
        const timeText = `${new Intl.DateTimeFormat(locale, {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(start)} – ${new Intl.DateTimeFormat(locale, { timeStyle: 'short' }).format(end)}`
        const current = (option.viewerVote as VoteChoiceValue | null) ?? null
        // An in-flight vote on THIS row must never disable its controls — a disabled button
        // drops browser focus outright, which would break revise-keeps-focus (design §6 S6
        // gate) the instant a click's own mutation goes pending. Only the closed-presentation
        // `disabled` prop disables controls; `pendingOptionId` only marks the row `aria-busy`.
        const rowBusy = pendingOptionId === option.id

        return (
          <li
            key={option.id}
            className="flex flex-col gap-1 border-t border-ink/25 py-3 first:border-t-0"
            data-testid={`poll-option-${option.id}`}
          >
            <p className="font-display text-h3 font-semibold text-ink">{timeText}</p>
            <p
              className="font-body text-meta text-ink-soft"
              data-testid={`poll-option-tally-${option.id}`}
            >
              {optionTallyText(option, t)}
            </p>
            {/* Text only — the `<li>` class list above stays unconditional (D-140). */}
            <p className="font-body text-meta text-ink">
              {current
                ? t('detail.yourVote', { value: t(`vote.${current}`) })
                : t('detail.noVoteYet')}
            </p>
            <div
              aria-busy={rowBusy}
              aria-label={t('vote.legend', { time: timeText })}
              className="mt-2 flex flex-wrap gap-2"
              data-testid={`vote-radiogroup-${option.id}`}
              role="radiogroup"
            >
              {CHOICES.map((choice) => {
                const checked = current === choice
                return (
                  <button
                    key={choice}
                    aria-checked={checked}
                    aria-label={t('vote.controlLabel', {
                      time: timeText,
                      value: t(`vote.${choice}`),
                    })}
                    className={`min-h-11 min-w-11 rounded-control border-2 border-ink bg-card px-3 py-2 font-display text-sm font-bold text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50 ${fillClasses[choice]} ${checked ? 'ring-2 ring-inset ring-ink' : ''}`}
                    data-testid={`vote-${choice}-${option.id}`}
                    disabled={disabled}
                    role="radio"
                    tabIndex={checked || (!current && choice === 'available') ? 0 : -1}
                    type="button"
                    onClick={() => !disabled && onVote(option.id, choice)}
                    onKeyDown={(event) => onKeyDown(event, option.id, current)}
                  >
                    <span aria-hidden="true">{glyph[choice]}</span>
                  </button>
                )
              })}
            </div>
          </li>
        )
      })}
    </ul>
  )
}
