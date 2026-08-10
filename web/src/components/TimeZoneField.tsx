/* eslint-disable react-refresh/only-export-components -- shared field plus its zone-list/validation helpers, not a route component */
import type { InputHTMLAttributes } from 'react'
import { useMemo } from 'react'
import { Input } from './ui/input'
import { Label } from './ui/label'

export interface TimeZoneOption {
  id: string
  label: string
}

function resolvedBrowserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone
  } catch {
    return 'UTC'
  }
}

function baseZoneIds(): string[] {
  if (typeof Intl.supportedValuesOf === 'function') {
    try {
      return Intl.supportedValuesOf('timeZone')
    } catch {
      // fall through to the degraded fallback below
    }
  }
  const browserZone = resolvedBrowserZone()
  return browserZone === 'UTC' ? ['UTC'] : [browserZone, 'UTC']
}

function formatOffset(zoneId: string): string | null {
  try {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone: zoneId,
      timeZoneName: 'shortOffset',
    }).formatToParts(new Date())
    return parts.find((part) => part.type === 'timeZoneName')?.value ?? null
  } catch {
    // One malformed/unrecognized zone id must not blank the rest of the list.
    return null
  }
}

function toOption(zoneId: string): TimeZoneOption {
  const offset = formatOffset(zoneId)
  return { id: zoneId, label: offset ? `${zoneId} (${offset})` : zoneId }
}

// Computed once per module load (~420 zones): offset formatting is too
// expensive to redo on every keystroke or render.
const BASE_ZONE_IDS = (() => {
  const ids = new Set(baseZoneIds())
  ids.add('UTC')
  // The browser may resolve an alias ("Asia/Calcutta") that supportedValuesOf reports only
  // canonically ("Asia/Kolkata"). Every surface prefills this value, so without it in the list
  // the field would reject its own default and strand the user.
  ids.add(resolvedBrowserZone())
  return [...ids]
})()
const BASE_ZONE_OPTIONS: TimeZoneOption[] = BASE_ZONE_IDS.map(toOption).sort((a, b) =>
  a.id.localeCompare(b.id),
)
const BASE_ZONE_ID_SET = new Set(BASE_ZONE_IDS)

/**
 * The full zone list, plus `storedZone` if it is not already present. Server
 * and browser tzdata copies can disagree, so a stored-but-unlisted zone must
 * stay selectable and valid rather than locking the owner out of their own
 * profile.
 */
export function getTimeZoneOptions(storedZone?: string | null): TimeZoneOption[] {
  if (!storedZone || BASE_ZONE_ID_SET.has(storedZone)) return BASE_ZONE_OPTIONS
  return [...BASE_ZONE_OPTIONS, toOption(storedZone)].sort((a, b) => a.id.localeCompare(b.id))
}

export function isKnownTimeZone(value: string, storedZone?: string | null): boolean {
  if (!value) return false
  if (BASE_ZONE_ID_SET.has(value)) return true
  return storedZone != null && value === storedZone
}

export interface TimeZoneFieldProps
  extends Omit<InputHTMLAttributes<HTMLInputElement>, 'list' | 'type'> {
  label: string
  hint: string
  storedZone?: string | null
  errorMessage?: string
}

export function TimeZoneField({
  id,
  label,
  hint,
  storedZone,
  errorMessage,
  'aria-describedby': ariaDescribedBy,
  ...props
}: TimeZoneFieldProps) {
  const options = useMemo(() => getTimeZoneOptions(storedZone), [storedZone])
  const datalistId = `${id}-options`
  const hintId = `${id}-hint`
  const errorId = `${id}-error`
  const describedBy = [hintId, errorMessage ? errorId : null, ariaDescribedBy]
    .filter(Boolean)
    .join(' ')

  return (
    <>
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        aria-describedby={describedBy || undefined}
        aria-invalid={errorMessage ? true : undefined}
        list={datalistId}
        type="text"
        {...props}
      />
      <datalist id={datalistId}>
        {options.map((option) => (
          <option key={option.id} value={option.id}>
            {option.label}
          </option>
        ))}
      </datalist>
      <p className="text-meta text-ink-soft" id={hintId}>
        {hint}
      </p>
      {errorMessage && (
        <p id={errorId} role="alert">
          {errorMessage}
        </p>
      )}
    </>
  )
}
