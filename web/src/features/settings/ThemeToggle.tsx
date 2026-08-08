import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  getStoredThemePreference,
  setThemePreference,
  watchSystemTheme,
  type ThemePreference,
} from '../../theme'

const OPTIONS: ThemePreference[] = ['system', 'light', 'dark']

/**
 * D-181 (PHASE5_1_DESIGN.md §6): the phase's single scoped non-styling
 * addition. Preference persists to localStorage via theme.ts (S1); this
 * component only owns the control and keeps the resolved `data-theme`
 * attribute live while a 'system' preference is selected and the OS scheme
 * changes underneath it.
 */
export function ThemeToggle() {
  const { t } = useTranslation('settings')
  const [preference, setPreference] = useState<ThemePreference>(() =>
    getStoredThemePreference(),
  )

  useEffect(() => watchSystemTheme(), [])

  function choose(next: ThemePreference) {
    setThemePreference(next)
    setPreference(next)
  }

  // ARIA APG radiogroup: arrows move selection, only the checked option is
  // in the tab order.
  function onKeyDown(event: React.KeyboardEvent<HTMLButtonElement>) {
    const delta =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0
    if (delta === 0) return
    event.preventDefault()
    const next =
      OPTIONS[
        (OPTIONS.indexOf(preference) + delta + OPTIONS.length) % OPTIONS.length
      ]
    choose(next)
    event.currentTarget.parentElement
      ?.querySelectorAll<HTMLButtonElement>('[role=radio]')
      [OPTIONS.indexOf(next)]?.focus()
  }

  return (
    <div
      aria-label={t('theme.legend')}
      className="flex flex-wrap gap-2"
      role="radiogroup"
    >
      {OPTIONS.map((option) => (
        <button
          key={option}
          aria-checked={preference === option}
          className="min-h-11 rounded-full border-2 border-ink px-4 py-2 font-display text-sm font-bold text-ink aria-checked:bg-accent aria-checked:text-accent-ink dark:aria-checked:border-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          role="radio"
          tabIndex={preference === option ? 0 : -1}
          type="button"
          onClick={() => choose(option)}
          onKeyDown={onKeyDown}
        >
          {t(`theme.options.${option}`)}
        </button>
      ))}
    </div>
  )
}
