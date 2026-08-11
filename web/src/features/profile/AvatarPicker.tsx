import { type JSX, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'

import { AVATAR_CATALOG } from './avatarCatalog'

/**
 * AVATARS_DESIGN.md §2.1: a 1-D wrapping ARIA APG radiogroup, the same arrow-key
 * idiom as ThemeToggle/VotingGrid. A 2-D roving grid is deliberately not used —
 * the catalog is one screen and needs no row/column model.
 *
 * `labelledBy` points at page text that already names the field; passing it
 * suppresses `picker.label` so the group never carries two competing names.
 */
export function AvatarPicker({
  value,
  onChange,
  idPrefix,
  labelledBy,
}: {
  value: string
  onChange: (glyph: string) => void
  idPrefix: string
  labelledBy?: string
}): JSX.Element {
  const { t } = useTranslation('avatar')
  const selectedIndex = AVATAR_CATALOG.findIndex((entry) => entry.glyph === value)

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
    const nextIndex =
      (from + delta + AVATAR_CATALOG.length) % AVATAR_CATALOG.length
    onChange(AVATAR_CATALOG[nextIndex].glyph)
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
      {AVATAR_CATALOG.map((entry, index) => {
        const checked = entry.glyph === value
        return (
          <button
            key={entry.slug}
            aria-checked={checked}
            aria-label={t(`catalog.${entry.slug}`)}
            className="min-h-11 min-w-11 rounded-control border-2 border-ink bg-card px-3 py-2 text-lg text-ink aria-checked:bg-accent aria-checked:text-accent-ink dark:aria-checked:border-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            data-testid={`${idPrefix}-${entry.slug}`}
            id={`${idPrefix}-${entry.slug}`}
            role="radio"
            tabIndex={checked || (selectedIndex < 0 && index === 0) ? 0 : -1}
            type="button"
            onClick={() => onChange(entry.glyph)}
            onKeyDown={onKeyDown}
          >
            <span aria-hidden="true">{entry.glyph}</span>
          </button>
        )
      })}
    </div>
  )
}
