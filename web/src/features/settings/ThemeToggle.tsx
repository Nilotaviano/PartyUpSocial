import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { segmentItemClasses, segmentStripClasses } from '../../components/ui/classes'
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
    const radios =
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>(
        '[role=radio]',
      )
    radios?.[OPTIONS.indexOf(next)]?.focus()
  }

  return (
    // SegmentStrip skin (§3.2) applied to this radiogroup directly rather than
    // through the wrapper component: the roving-focus lookup below walks
    // `parentElement`, so the strip has to *be* the radiogroup, not wrap it.
    <div
      aria-label={t('theme.legend')}
      className={`${segmentStripClasses} max-w-form`}
      role="radiogroup"
    >
      {OPTIONS.map((option) => (
        <button
          key={option}
          aria-checked={preference === option}
          className={segmentItemClasses(preference === option, 'min-w-11')}
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
