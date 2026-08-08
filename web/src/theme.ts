export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export const THEME_STORAGE_KEY = 'partyup:theme'

const DARK_MEDIA_QUERY = '(prefers-color-scheme: dark)'

function isThemePreference(value: string | null): value is ThemePreference {
  return value === 'system' || value === 'light' || value === 'dark'
}

export function getStoredThemePreference(): ThemePreference {
  if (typeof localStorage === 'undefined') {
    return 'system'
  }

  const stored = localStorage.getItem(THEME_STORAGE_KEY)
  return isThemePreference(stored) ? stored : 'system'
}

export function getSystemTheme(): ResolvedTheme {
  if (typeof matchMedia === 'undefined') {
    return 'light'
  }

  return matchMedia(DARK_MEDIA_QUERY).matches ? 'dark' : 'light'
}

export function resolveTheme(preference: ThemePreference): ResolvedTheme {
  return preference === 'system' ? getSystemTheme() : preference
}

export function applyTheme(resolved: ResolvedTheme) {
  document.documentElement.setAttribute('data-theme', resolved)
}

export function setThemePreference(preference: ThemePreference) {
  if (preference === 'system') {
    localStorage.removeItem(THEME_STORAGE_KEY)
  } else {
    localStorage.setItem(THEME_STORAGE_KEY, preference)
  }

  applyTheme(resolveTheme(preference))
}

/**
 * Re-applies the resolved theme whenever the OS-level color scheme changes,
 * but only while the stored preference is 'system' (an explicit light/dark
 * override is never overridden by an OS change).
 */
export function watchSystemTheme(onChange?: (resolved: ResolvedTheme) => void) {
  if (typeof matchMedia === 'undefined') {
    return () => {}
  }

  const media = matchMedia(DARK_MEDIA_QUERY)
  const listener = () => {
    if (getStoredThemePreference() !== 'system') {
      return
    }

    const resolved = getSystemTheme()
    applyTheme(resolved)
    onChange?.(resolved)
  }

  media.addEventListener('change', listener)
  return () => media.removeEventListener('change', listener)
}
