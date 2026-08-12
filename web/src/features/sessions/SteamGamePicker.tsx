import { useQuery } from '@tanstack/react-query'
import { useEffect, useId, useState } from 'react'
import type { TFunction } from 'i18next'
import { extractSteamAppId } from './steamAppIdField'
import { searchSteamCatalog, steamCatalogKeys } from './steamCatalogQueries'

const DEBOUNCE_MS = 300
const MIN_QUERY_LENGTH = 2

const inputClasses =
  'mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:bg-busy disabled:text-ink-soft disabled:opacity-100'

export type SteamGamePickerResult = { name: string; appId: string }

export type SteamGamePickerProps = {
  /** The parent page's own `useTranslation` output — reuses its namespace
   * (`sessions` or `polls`) so the two i18n files can each carry the same
   * relative key shape (`create.gamePicker.*`) instead of a shared one. */
  t: TFunction
  nameId: string
  appIdId: string
  nameValue: string
  appIdValue: string
  nameError?: string
  appIdError?: string
  disabled?: boolean
  onNameChange: (name: string) => void
  onAppIdChange: (appId: string) => void
  onSelect: (result: SteamGamePickerResult) => void
}

/**
 * D-194 §4: the Steam arm's game picker. WAI-ARIA combobox (input
 * `role="combobox"` + a `role="listbox"` popup, `aria-activedescendant`
 * keyboard nav, Escape closes) while the catalog is known-available;
 * degrades to the pre-D-194 honest pair (free-text name + optional manual
 * App ID field, `extractSteamAppId`) the first time a search response
 * carries `catalogAvailable: false` or the search request errors. Once
 * degraded, the component never re-probes — the fallback is sticky for the
 * life of the form (design §4, "fallback-only").
 */
export function SteamGamePicker({
  t,
  nameId,
  appIdId,
  nameValue,
  appIdValue,
  nameError,
  appIdError,
  disabled = false,
  onNameChange,
  onAppIdChange,
  onSelect,
}: SteamGamePickerProps) {
  const listboxId = useId()
  const [debouncedTerm, setDebouncedTerm] = useState(nameValue)
  const [popupOpen, setPopupOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(-1)
  const [degraded, setDegraded] = useState(false)

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedTerm(nameValue), DEBOUNCE_MS)
    return () => window.clearTimeout(handle)
  }, [nameValue])

  const trimmedTerm = debouncedTerm.trim()
  const searchEnabled =
    !disabled && !degraded && trimmedTerm.length >= MIN_QUERY_LENGTH

  const search = useQuery({
    queryKey: steamCatalogKeys.search(trimmedTerm),
    queryFn: () => searchSteamCatalog(trimmedTerm),
    enabled: searchEnabled,
  })

  // Adjusting state during render (not in an effect), the same documented
  // pattern used by SessionEditPage's form-seeding block: the fallback is
  // sticky once tripped, so this only ever flips `degraded` from false to
  // true, never oscillates.
  if (!degraded && ((search.data && !search.data.catalogAvailable) || search.isError)) {
    setDegraded(true)
  }

  const [lastTerm, setLastTerm] = useState(trimmedTerm)
  if (trimmedTerm !== lastTerm) {
    setLastTerm(trimmedTerm)
    if (activeIndex !== -1) setActiveIndex(-1)
  }

  const results = search.data?.results ?? []
  const showPopup =
    popupOpen && !degraded && trimmedTerm.length >= MIN_QUERY_LENGTH
  // F5/axe aria-valid-attr-value: aria-controls must reference an element
  // that actually exists in the DOM. The listbox <ul id={listboxId}> only
  // renders once results are in hand — while loading or empty, no element
  // has that id, so aria-controls must be withheld to match.
  const listboxRendered = showPopup && !search.isFetching && results.length > 0

  function selectResult(result: SteamGamePickerResult) {
    onSelect(result)
    setPopupOpen(false)
    setActiveIndex(-1)
  }

  function handleKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (degraded) return

    if (event.key === 'ArrowDown') {
      event.preventDefault()
      if (!showPopup) {
        setPopupOpen(true)
        return
      }
      if (results.length === 0) return
      setActiveIndex((current) => (current + 1) % results.length)
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      if (!showPopup || results.length === 0) return
      setActiveIndex((current) => (current - 1 + results.length) % results.length)
    } else if (event.key === 'Enter') {
      if (showPopup && activeIndex >= 0 && results[activeIndex]) {
        event.preventDefault()
        selectResult(results[activeIndex])
      }
    } else if (event.key === 'Escape') {
      if (popupOpen) {
        event.preventDefault()
        setPopupOpen(false)
        setActiveIndex(-1)
      }
    }
  }

  if (degraded) {
    return (
      <>
        <label className="mt-2 block" htmlFor={nameId}>
          {t('create.gameFreeText')}
        </label>
        <input
          aria-describedby={nameError ? `${nameId}-error` : undefined}
          aria-invalid={Boolean(nameError)}
          className={inputClasses}
          disabled={disabled}
          id={nameId}
          value={nameValue}
          onChange={(event) => onNameChange(event.target.value)}
        />
        {nameError && (
          <p id={`${nameId}-error`} role="alert">
            {nameError}
          </p>
        )}
        <div className="mt-2">
          <label className="block font-body font-semibold text-ink" htmlFor={appIdId}>
            {t('create.steamAppId')}
          </label>
          <input
            aria-describedby={appIdError ? `${appIdId}-error` : `${appIdId}-help`}
            aria-invalid={Boolean(appIdError)}
            className={inputClasses}
            disabled={disabled}
            id={appIdId}
            inputMode="numeric"
            value={appIdValue}
            onChange={(event) => onAppIdChange(extractSteamAppId(event.target.value))}
          />
          <p className="mt-1 text-ink-soft" id={`${appIdId}-help`}>
            {t('create.steamAppIdHelp')}
          </p>
          {appIdError && (
            <p id={`${appIdId}-error`} role="alert">
              {appIdError}
            </p>
          )}
        </div>
      </>
    )
  }

  return (
    <div className="relative">
      <label className="mt-2 block" htmlFor={nameId}>
        {t('create.gamePicker.label')}
      </label>
      <input
        aria-activedescendant={
          activeIndex >= 0 ? `${listboxId}-option-${activeIndex}` : undefined
        }
        aria-autocomplete="list"
        aria-controls={listboxRendered ? listboxId : undefined}
        aria-describedby={nameError ? `${nameId}-error` : undefined}
        aria-expanded={showPopup}
        aria-invalid={Boolean(nameError)}
        autoComplete="off"
        className={inputClasses}
        disabled={disabled}
        id={nameId}
        placeholder={t('create.gamePicker.placeholder')}
        role="combobox"
        value={nameValue}
        onBlur={() => {
          window.setTimeout(() => setPopupOpen(false), 100)
        }}
        onChange={(event) => {
          onNameChange(event.target.value)
          onAppIdChange('')
          setPopupOpen(true)
        }}
        onFocus={() => {
          if (trimmedTerm.length >= MIN_QUERY_LENGTH) setPopupOpen(true)
        }}
        onKeyDown={handleKeyDown}
      />
      {nameError && (
        <p id={`${nameId}-error`} role="alert">
          {nameError}
        </p>
      )}
      {showPopup && (
        <div className="absolute z-10 mt-1 w-full rounded-control border-2 border-ink bg-card shadow-offset-dialog">
          {search.isFetching && (
            <p className="p-2 text-ink-soft" role="status">
              {t('create.gamePicker.loading')}
            </p>
          )}
          {!search.isFetching && results.length === 0 && (
            <p className="p-2 text-ink-soft">{t('create.gamePicker.noResults')}</p>
          )}
          {!search.isFetching && results.length > 0 && (
            <ul
              aria-label={t('create.gamePicker.listboxLabel')}
              className="max-h-60 overflow-y-auto"
              id={listboxId}
              role="listbox"
            >
              {results.map((result, index) => (
                <li
                  key={result.appId}
                  aria-selected={index === activeIndex}
                  className={`cursor-pointer p-2 text-ink ${
                    index === activeIndex ? 'bg-busy' : ''
                  }`}
                  id={`${listboxId}-option-${index}`}
                  role="option"
                  onMouseDown={(event) => {
                    event.preventDefault()
                    selectResult(result)
                  }}
                >
                  {result.name}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
