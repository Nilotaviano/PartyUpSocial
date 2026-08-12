import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { useDirtyGuard } from '../../components/useDirtyGuard'
import { DiscardDialog } from './SessionCreatePage'
import {
  editSessionWithConflictRetry,
  getSessionDetail,
  invalidateSessionQueries,
  isSessionInProgress,
  sessionKeys,
  type ClearField,
  type EditSessionRequest,
  type SessionDetail,
} from './sessionQueries'
import { SteamGamePicker } from './SteamGamePicker'

// Single source of truth for the validation-summary link targets: every
// entry in `fieldErrors` must resolve to a real element id (H2).
const fieldIds: Record<string, string> = {
  title: 'field-title',
  minAttendance: 'field-min',
  gameAppId: 'field-game-app-id',
}

function formFrom(session: SessionDetail) {
  return {
    title: session.title,
    start: session.startTime.slice(0, 16),
    durationMinutes: String(session.durationMinutes),
    platform: session.platform,
    gameName: session.game?.name ?? '',
    gameAppId: session.game?.steamAppId ? String(session.game.steamAppId) : '',
    notes: session.notes,
    minAttendance: String(session.minAttendance),
    maxCapacity:
      session.maxCapacity === null ? '' : String(session.maxCapacity),
    isPrivate: session.isPrivate,
    // The single unlimited-capacity control (A-070): pre-checked when the
    // session already has no maximum, mapped onto the PATCH `clear` list
    // rather than a competing `maxCapacity: null` body value.
    clearMax: session.maxCapacity === null,
    clearNotes: false,
    clearGame: false,
  }
}

export function SessionEditPage() {
  const { t } = useTranslation(['sessions', 'errors', 'common'])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { sessionId = '' } = useParams()
  const heading = useRef<HTMLHeadingElement>(null)
  const summaryRef = useRef<HTMLDivElement>(null)
  const cancelTrigger = useRef<HTMLButtonElement>(null)
  const [form, setForm] = useState<ReturnType<typeof formFrom>>()
  const [dirty, setDirty] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [discardOpen, setDiscardOpen] = useState(false)
  const [conflictNotice, setConflictNotice] = useState(false)
  const [pendingNavigation, setPendingNavigation] = useState<string | null>(null)

  const detail = useQuery({
    queryKey: sessionKeys.detail(sessionId),
    queryFn: () => getSessionDetail(sessionId),
    enabled: Boolean(sessionId),
    retry: false,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  useDirtyGuard(dirty, {
    onBlockedNavigation: (target) => {
      setPendingNavigation(target)
      setDiscardOpen(true)
    },
  })

  // Adjusting state during render (not in an effect) to seed the form the
  // first time the detail query resolves, per React's documented pattern
  // for deriving state from data that only becomes available after mount.
  if (detail.data && !form) {
    setForm(formFrom(detail.data))
  }

  // The app-id field only renders when a game is kept on a Steam platform
  // (see the JSX below); once either condition stops holding, a stale
  // `gameAppId` error would anchor the validation summary at a field that
  // no longer exists, breaking the `_H2` invariant. Cleared during render,
  // the same documented pattern as the form-seeding block above.
  if (form && (form.clearGame || form.platform !== 'Steam') && 'gameAppId' in fieldErrors) {
    setFieldErrors((current) => {
      const rest = { ...current }
      delete rest.gameAppId
      return rest
    })
  }

  const save = useMutation({
    mutationFn: async () => {
      if (!form || !detail.data) throw new Error('Form not ready.')
      const clear: ClearField[] = []
      if (form.clearMax) clear.push('maxCapacity')
      if (form.clearNotes) clear.push('notes')
      if (form.clearGame) clear.push('game')

      // A-070: a field named in `clear` must not also appear in the body.
      // Cleared fields are therefore omitted here, never sent as `null`
      // alongside their own `clear` entry.
      const body: Record<string, unknown> = {
        title: form.title,
        startTime: new Date(form.start).toISOString(),
        durationMinutes: Number(form.durationMinutes),
        platform: form.platform || null,
        minAttendance: Number(form.minAttendance),
        isPrivate: form.isPrivate,
        clear,
        ifMatch: detail.data.version,
      }
      if (!form.clearMax) body.maxCapacity = Number(form.maxCapacity)
      if (!form.clearNotes) body.notes = form.notes
      if (!form.clearGame) {
        body.game = form.gameName
          ? {
              name: form.gameName,
              steamAppId:
                form.platform === 'Steam' && form.gameAppId ? form.gameAppId : null,
            }
          : null
      }

      try {
        return await editSessionWithConflictRetry(
          queryClient,
          sessionId,
          body as EditSessionRequest,
        )
      } catch (error) {
        if (isApiError(error) && error.code === 'session_conflict') {
          setConflictNotice(true)
        }
        throw error
      }
    },
    onSuccess: async () => {
      setDirty(false)
      await invalidateSessionQueries(queryClient, sessionId)
      navigate(`/sessions/${sessionId}`)
    },
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/welcome" />
  }

  function update<K extends keyof NonNullable<typeof form>>(
    key: K,
    value: NonNullable<typeof form>[K],
  ) {
    setForm((current) => (current ? { ...current, [key]: value } : current))
    setDirty(true)
  }

  function validate(): Record<string, string> {
    const errors: Record<string, string> = {}
    if (!form) return errors
    if (!form.title.trim()) errors.title = t('validation.required')
    const min = Number(form.minAttendance)
    const max = form.clearMax ? undefined : Number(form.maxCapacity)
    if (!Number.isFinite(min) || min < 1) errors.minAttendance = t('validation.range')
    if (max !== undefined && min > max) errors.minAttendance = t('validation.minMax')
    if (
      !form.clearGame &&
      form.platform === 'Steam' &&
      form.gameAppId &&
      !/^\d+$/.test(form.gameAppId)
    ) {
      errors.gameAppId = t('validation.steamAppIdDigits')
    }
    return errors
  }

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault()
    const errors = validate()
    setFieldErrors(errors)
    if (Object.keys(errors).length > 0) {
      summaryRef.current?.focus()
      return
    }
    setConflictNotice(false)
    save.mutate()
  }

  function requestCancel() {
    if (dirty) {
      setPendingNavigation(`/sessions/${sessionId}`)
      setDiscardOpen(true)
      return
    }
    navigate(`/sessions/${sessionId}`)
  }

  const locked = detail.data ? isSessionInProgress(detail.data) : false

  return (
    <main
      className="mx-auto max-w-2xl space-y-6 p-4 sm:p-8"
      data-testid="screen-session-edit"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('edit.heading')}
      </h1>

      {detail.isPending && (
        <p role="status">{t('common.loading', { ns: 'common' })}</p>
      )}
      {detail.error && !form && (
        <p role="alert">
          {t(
            isApiError(detail.error)
              ? `errors.codes.${detail.error.code}`
              : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      {conflictNotice && (
        <p aria-live="polite">{t('edit.conflictRetry')}</p>
      )}

      {Object.keys(fieldErrors).length > 0 && (
        <div
          ref={summaryRef}
          className="rounded-card border-2 border-danger bg-danger/10 p-4"
          role="alert"
          tabIndex={-1}
        >
          <p className="font-display font-semibold text-ink">
            {t('a11y.validationSummary')}
          </p>
          <ul className="mt-2 list-disc pl-5 text-ink">
            {Object.entries(fieldErrors).map(([field, message]) => (
              <li key={field}>
                <a
                  className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                  href={`#${fieldIds[field] ?? `field-${field}`}`}
                >
                  {message}
                </a>
              </li>
            ))}
          </ul>
        </div>
      )}

      {save.error && (
        <p role="alert">
          {t(
            isApiError(save.error)
              ? `errors.codes.${save.error.code}`
              : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      {form && (
        <form className="space-y-5" noValidate onSubmit={handleSubmit}>
          <div>
            <label className="block font-body font-semibold text-ink" htmlFor="field-title">
              {t('create.title')}
            </label>
            <input
              aria-invalid={Boolean(fieldErrors.title)}
              className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
              id="field-title"
              value={form.title}
              onChange={(event) => update('title', event.target.value)}
            />
            {fieldErrors.title && <p role="alert">{fieldErrors.title}</p>}
          </div>

          <div>
            <label className="block font-body font-semibold text-ink" htmlFor="field-start">
              {t('create.start')}
            </label>
            {locked ? (
              <p>{t('edit.startLocked')}</p>
            ) : (
              <input
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="field-start"
                step={900}
                type="datetime-local"
                value={form.start}
                onChange={(event) => update('start', event.target.value)}
              />
            )}
          </div>

          <div>
            <label
              className="block font-body font-semibold text-ink"
              htmlFor="field-min"
            >
              {t('create.min')}
            </label>
            {locked ? (
              <p>{t('edit.minLocked')}</p>
            ) : (
              <input
                aria-invalid={Boolean(fieldErrors.minAttendance)}
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="field-min"
                min={1}
                type="number"
                value={form.minAttendance}
                onChange={(event) => update('minAttendance', event.target.value)}
              />
            )}
            {fieldErrors.minAttendance && (
              <p role="alert">{fieldErrors.minAttendance}</p>
            )}
          </div>

          <div>
            <label className="block font-body font-semibold text-ink" htmlFor="field-max">
              {t('create.max')}
            </label>
            <input
              className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:bg-busy disabled:text-ink-soft disabled:opacity-100"
              disabled={form.clearMax}
              id="field-max"
              min={1}
              type="number"
              value={form.maxCapacity}
              onChange={(event) => update('maxCapacity', event.target.value)}
            />
            <label className="mt-2 block inline-flex min-h-11 items-center gap-2">
              <input
                checked={form.clearMax}
                type="checkbox"
                onChange={(event) => update('clearMax', event.target.checked)}
              />
              {t('edit.clearMax')}
            </label>
          </div>

          <div>
            <label className="block font-body font-semibold text-ink" htmlFor="field-notes">
              {t('create.notes')}
            </label>
            <textarea
              className="mt-1 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:bg-busy disabled:text-ink-soft disabled:opacity-100"
              disabled={form.clearNotes}
              id="field-notes"
              value={form.notes}
              onChange={(event) => update('notes', event.target.value)}
            />
            <label className="mt-2 inline-flex min-h-11 items-center gap-2">
              <input
                checked={form.clearNotes}
                type="checkbox"
                onChange={(event) => update('clearNotes', event.target.checked)}
              />
              {t('edit.clearNotes')}
            </label>
          </div>

          <div>
            {form.platform === 'Steam' && !form.clearGame ? (
              <p className="block font-body font-semibold text-ink">
                {t('create.game')}
              </p>
            ) : (
              <label className="block font-body font-semibold text-ink" htmlFor="field-game">
                {t('create.game')}
              </label>
            )}
            {form.platform === 'Steam' && !form.clearGame ? (
              <SteamGamePicker
                appIdError={fieldErrors.gameAppId}
                appIdId="field-game-app-id"
                appIdValue={form.gameAppId}
                nameError={fieldErrors.gameName}
                nameId="field-game"
                nameValue={form.gameName}
                t={t}
                onAppIdChange={(appId) => update('gameAppId', appId)}
                onNameChange={(name) => update('gameName', name)}
                onSelect={({ name, appId }) => {
                  setForm((current) =>
                    current ? { ...current, gameName: name, gameAppId: appId } : current,
                  )
                  setDirty(true)
                }}
              />
            ) : (
              <input
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:bg-busy disabled:text-ink-soft disabled:opacity-100"
                disabled={form.clearGame}
                id="field-game"
                value={form.gameName}
                onChange={(event) => update('gameName', event.target.value)}
              />
            )}
            <label className="mt-2 inline-flex min-h-11 items-center gap-2">
              <input
                checked={form.clearGame}
                type="checkbox"
                onChange={(event) => update('clearGame', event.target.checked)}
              />
              {t('edit.clearGame')}
            </label>
          </div>

          <div>
            <label className="inline-flex min-h-11 items-center gap-2">
              <input
                checked={form.isPrivate}
                type="checkbox"
                onChange={(event) => update('isPrivate', event.target.checked)}
              />
              {t('create.private')}
            </label>
            <p className="mt-1 text-ink-soft">
              {t('edit.privacyDisclosure')}
            </p>
          </div>

          <div className="flex flex-wrap justify-end gap-2">
            <button
              ref={cancelTrigger}
              className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
              type="button"
              onClick={requestCancel}
            >
              {t('dirty.stay')}
            </button>
            <Button
              aria-busy={save.isPending}
              disabled={save.isPending}
              type="submit"
              variant="primary"
            >
              {t('edit.save')}
            </Button>
          </div>
        </form>
      )}

      <DiscardDialog
        open={discardOpen}
        triggerRef={cancelTrigger}
        onDiscard={() => {
          setDiscardOpen(false)
          setDirty(false)
          const target = pendingNavigation ?? `/sessions/${sessionId}`
          setPendingNavigation(null)
          if (target === 'back') {
            window.history.back()
          } else {
            navigate(target)
          }
        }}
        onOpenChange={setDiscardOpen}
      />
    </main>
  )
}
