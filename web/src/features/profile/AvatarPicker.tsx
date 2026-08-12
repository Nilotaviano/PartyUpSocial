import { type JSX, type KeyboardEvent, type ReactNode, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { AVATAR_CATALOG } from './avatarCatalog'
import { avatarSourceLabelKey, type AvatarSourceOption } from './avatarSources'

type PickerOption = {
  key: string
  value: string
  ariaLabel: string
  node: ReactNode
}

/**
 * AVATARS_DESIGN.md §2.1: a 1-D wrapping ARIA APG radiogroup, the same arrow-key
 * idiom as ThemeToggle/VotingGrid. A 2-D roving grid is deliberately not used —
 * the catalog is one screen and needs no row/column model.
 *
 * `labelledBy` points at page text that already names the field; passing it
 * suppresses `picker.label` so the group never carries two competing names.
 *
 * AVATARS_DESIGN.md §3.5: `sources` extends this same radiogroup with the caller's
 * linked-account pictures, appended after the emoji catalog — one component, one
 * group, never a second picker. Onboarding never passes `sources` (§2.1: no avatar
 * source can exist yet at that point), so it renders exactly as before.
 */
export function AvatarPicker({
  value,
  onChange,
  idPrefix,
  labelledBy,
  sources = [],
}: {
  value: string
  onChange: (value: string) => void
  idPrefix: string
  labelledBy?: string
  sources?: AvatarSourceOption[]
}): JSX.Element {
  const { t } = useTranslation('avatar')
  // AVATARS_DESIGN.md §3.5's onError contract (also AvatarDisc): a source
  // thumbnail that fails to load falls back to the neutral '●' glyph rather
  // than the browser's broken-image icon or a blank radio.
  const [failedSourceIds, setFailedSourceIds] = useState<Set<string>>(new Set())

  const catalogOptions: PickerOption[] = AVATAR_CATALOG.map((entry) => ({
    key: entry.slug,
    value: entry.glyph,
    ariaLabel: t(`catalog.${entry.slug}`),
    node: <span aria-hidden="true">{entry.glyph}</span>,
  }))
  const sourceOptions: PickerOption[] = sources.map((source) => ({
    key: source.avatarSourceId,
    value: `source:${source.avatarSourceId}`,
    ariaLabel: t(`picker.source.${avatarSourceLabelKey(source.kind)}`, {
      defaultValue: t('picker.source.fallback'),
    }),
    node: (
      <span aria-hidden="true">
        {failedSourceIds.has(source.avatarSourceId) ? (
          '●'
        ) : (
          <img
            alt=""
            className="h-6 w-6 rounded-full object-cover"
            src={source.imgUrl}
            onError={() =>
              setFailedSourceIds((current) => new Set(current).add(source.avatarSourceId))
            }
          />
        )}
      </span>
    ),
  }))
  const options = [...catalogOptions, ...sourceOptions]
  const selectedIndex = options.findIndex((option) => option.value === value)

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const delta =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0
    if (delta === 0) return
    event.preventDefault()
    const from = selectedIndex < 0 ? 0 : selectedIndex
    const nextIndex = (from + delta + options.length) % options.length
    onChange(options[nextIndex].value)
    const radios =
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>(
        '[role=radio]',
      )
    radios?.[nextIndex]?.focus()
  }

  return (
    <div
      aria-label={labelledBy ? undefined : t('picker.label')}
      aria-labelledby={labelledBy}
      className="flex flex-wrap gap-2"
      data-testid={`${idPrefix}-picker`}
      role="radiogroup"
    >
      {options.map((option, index) => {
        const checked = option.value === value
        return (
          <button
            key={option.key}
            aria-checked={checked}
            aria-label={option.ariaLabel}
            className="min-h-11 min-w-11 rounded-control border-2 border-ink bg-card px-3 py-2 text-lg text-ink aria-checked:bg-accent aria-checked:text-accent-ink dark:aria-checked:border-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            data-testid={`${idPrefix}-${option.key}`}
            id={`${idPrefix}-${option.key}`}
            role="radio"
            tabIndex={checked || (selectedIndex < 0 && index === 0) ? 0 : -1}
            type="button"
            onClick={() => onChange(option.value)}
            onKeyDown={onKeyDown}
          >
            {option.node}
          </button>
        )
      })}
    </div>
  )
}
