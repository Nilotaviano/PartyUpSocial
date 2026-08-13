import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  controlRowClasses,
  fieldControlClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { DateEcho } from '../../components/ui/dateEcho'
import { FormField, FormShell } from '../../components/ui/formShell'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
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
    <main data-testid="screen-session-edit">
      <PageScaffold width="form">
        <PageHeader title={t('edit.heading')} />

        {detail.isPending && <p role="status">{t('common.loading', { ns: 'common' })}</p>}
        {detail.error && !form && (
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(detail.error)
                ? `errors.codes.${detail.error.code}`
                : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        {conflictNotice && <p aria-live="polite">{t('edit.conflictRetry')}</p>}

        {save.error && (
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(save.error) ? `errors.codes.${save.error.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        {form && (
          <FormShell
            cancel={
              <button
                ref={cancelTrigger}
                className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
                onClick={requestCancel}
              >
                {t('dirty.stay')}
              </button>
            }
            header={
              Object.keys(fieldErrors).length > 0 ? (
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
              ) : undefined
            }
            submit={
              <Button
                aria-busy={save.isPending}
                disabled={save.isPending}
                type="submit"
                variant="primary"
              >
                {t('edit.save')}
              </Button>
            }
            onSubmit={handleSubmit}
          >
            <FormField htmlFor="field-title" label={t('create.title')}>
              <input
                aria-invalid={Boolean(fieldErrors.title)}
                className={fieldControlClasses}
                id="field-title"
                value={form.title}
                onChange={(event) => update('title', event.target.value)}
              />
              {fieldErrors.title && <p role="alert">{fieldErrors.title}</p>}
            </FormField>

            <FormField htmlFor="field-start" label={t('create.start')}>
              {locked ? (
                <p>{t('edit.startLocked')}</p>
              ) : (
                <>
                  <input
                    className={fieldControlClasses}
                    id="field-start"
                    step={900}
                    type="datetime-local"
                    value={form.start}
                    onChange={(event) => update('start', event.target.value)}
                  />
                  <DateEcho value={form.start} />
                </>
              )}
            </FormField>

            <FormField htmlFor="field-min" label={t('create.min')}>
              {locked ? (
                <p>{t('edit.minLocked')}</p>
              ) : (
                <input
                  aria-invalid={Boolean(fieldErrors.minAttendance)}
                  className={fieldControlClasses}
                  id="field-min"
                  min={1}
                  type="number"
                  value={form.minAttendance}
                  onChange={(event) => update('minAttendance', event.target.value)}
                />
              )}
              {fieldErrors.minAttendance && <p role="alert">{fieldErrors.minAttendance}</p>}
            </FormField>

            <FormField htmlFor="field-max" label={t('create.max')}>
              <input
                className={fieldControlClasses}
                disabled={form.clearMax}
                id="field-max"
                min={1}
                type="number"
                value={form.maxCapacity}
                onChange={(event) => update('maxCapacity', event.target.value)}
              />
              <label className={controlRowClasses}>
                <input
                  checked={form.clearMax}
                  type="checkbox"
                  onChange={(event) => update('clearMax', event.target.checked)}
                />
                {t('edit.clearMax')}
              </label>
            </FormField>

            <FormField htmlFor="field-notes" label={t('create.notes')}>
              <textarea
                className={fieldControlClasses}
                disabled={form.clearNotes}
                id="field-notes"
                value={form.notes}
                onChange={(event) => update('notes', event.target.value)}
              />
              <label className={controlRowClasses}>
                <input
                  checked={form.clearNotes}
                  type="checkbox"
                  onChange={(event) => update('clearNotes', event.target.checked)}
                />
                {t('edit.clearNotes')}
              </label>
            </FormField>

            <div className="flex flex-col gap-1.5">
              {form.platform === 'Steam' && !form.clearGame ? (
                <p className="block font-body font-semibold text-ink">{t('create.game')}</p>
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
                  className={fieldControlClasses}
                  disabled={form.clearGame}
                  id="field-game"
                  value={form.gameName}
                  onChange={(event) => update('gameName', event.target.value)}
                />
              )}
              <label className={controlRowClasses}>
                <input
                  checked={form.clearGame}
                  type="checkbox"
                  onChange={(event) => update('clearGame', event.target.checked)}
                />
                {t('edit.clearGame')}
              </label>
            </div>

            <div className="flex flex-col gap-1.5">
              <label className={controlRowClasses}>
                <input
                  checked={form.isPrivate}
                  type="checkbox"
                  onChange={(event) => update('isPrivate', event.target.checked)}
                />
                {t('create.private')}
              </label>
              <p className="font-body text-meta text-ink-soft">
                {t('edit.privacyDisclosure')}
              </p>
            </div>
          </FormShell>
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
      </PageScaffold>
    </main>
  )
}
