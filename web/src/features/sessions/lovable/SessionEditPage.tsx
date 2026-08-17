import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router'
import { CalendarClock, Gamepad2, Users } from 'lucide-react'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import { controlRowClasses, raisedInteractiveClasses } from '../../../components/ui/classes'
import { PageHeader } from '../../../components/shell/PageHeader'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { useDirtyGuard } from '../../../components/useDirtyGuard'
import { getGroupList, groupKeys } from '../../groups/groupQueries'
import { VisibilityControl, type Visibility } from '../../groups/VisibilityControl'
import {
  editSessionWithConflictRetry,
  getSessionDetail,
  invalidateSessionQueries,
  isSessionInProgress,
  sessionKeys,
  type ClearField,
  type EditSessionRequest,
  type SessionDetail,
} from '../sessionQueries'
import {
  Section,
  SessionAttendanceFieldset,
  SessionDetailsFieldset,
  SessionDiscardDialog,
  SessionGameField,
  SessionStartField,
  ValidationSummary,
} from './SessionFormBits'

/**
 * Shares its field-level pieces with `./SessionCreatePage` through
 * `./SessionFormBits` so the two forms cannot drift.
 *
 * Behavior carried by this page: D-194/D-176 (Steam catalog incl. degraded
 * arm, preserving the stored app id), D-123 (the "does not notify
 * participants" disclosure next to privacy), D-152/D-162 (dirty guard +
 * discard dialog), A-070 (the clear-list PATCH contract — a cleared field is
 * omitted from the body, not sent `null` alongside its own `clear` entry),
 * A-071 (retry-once-then-surface on `session_conflict`), and the
 * in-progress lock on start/minimum attendance.
 */

const fieldIds: Record<string, string> = {
  title: 'field-title',
  minAttendance: 'field-min',
  gameAppId: 'field-game-app-id',
  audienceGroupIds: 'field-visibility',
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
    maxCapacity: session.maxCapacity === null ? '' : String(session.maxCapacity),
    // The read model carries the viewer's own grant (`viaGroup`) and a neutral
    // `sharedWithGroup` flag (D-206), never the session's full audience-group
    // list — there is no read surface for that (mobile-clean contract). The
    // control below is seeded from `visibility` alone; touching it starts a
    // fresh group pick (`visibilityTouched`) rather than pretending to know
    // the current audience.
    visibility: session.visibility as Visibility,
    audienceGroupIds: [] as string[],
    visibilityTouched: false,
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

  // D-201: any group the actor owns or holds an Accepted membership in can target.
  const groupList = useQuery({ queryKey: groupKeys.list, queryFn: getGroupList })
  const targetableGroups = [
    ...(groupList.data?.owned ?? []),
    ...(groupList.data?.memberOf ?? []),
  ].map((group) => ({ id: group.id, name: group.name }))

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
  // (`SessionGameField` below); once either condition stops holding, a
  // stale `gameAppId` error would anchor the validation summary at a field
  // that no longer exists, breaking the `_H2` invariant. Cleared during
  // render, the same documented pattern as the form-seeding block above.
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
        clear,
        ifMatch: detail.data.version,
      }
      // The server leaves visibility/audience untouched when omitted (D-201) — only send them
      // once the control has actually been used, since the read model carries no full audience
      // list to safely resend unchanged (see `formFrom`'s comment above).
      if (form.visibilityTouched) {
        body.visibility = form.visibility
        body.audienceGroupIds = form.visibility === 'groups' ? form.audienceGroupIds : []
      }
      if (!form.clearMax) body.maxCapacity = Number(form.maxCapacity)
      if (!form.clearNotes) body.notes = form.notes
      if (!form.clearGame) {
        body.game = form.gameName
          ? {
              name: form.gameName,
              steamAppId: form.platform === 'Steam' && form.gameAppId ? form.gameAppId : null,
            }
          : null
      }

      try {
        return await editSessionWithConflictRetry(queryClient, sessionId, body as EditSessionRequest)
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
    if (form.visibilityTouched && form.visibility === 'groups' && form.audienceGroupIds.length === 0) {
      errors.audienceGroupIds = t('validation.pickAtLeastOneGroup')
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
    <main className="min-h-screen bg-background pb-32" data-testid="screen-session-edit">
      <div className="mx-auto max-w-[880px] px-3 sm:px-6">
        <PageHeader
          back={{ label: t('nav.backToSession'), to: `/sessions/${sessionId}` }}
          title={t('edit.heading')}
        />
      </div>

      <div className="mx-auto max-w-[880px] space-y-3 px-3 sm:px-6">
        {detail.isPending && <p role="status">{t('common.loading', { ns: 'common' })}</p>}
        {detail.error && !form && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(detail.error) ? `errors.codes.${detail.error.code}` : 'errors.generic', {
              defaultValue: t('errors.generic'),
            })}
          </StatusBanner>
        )}

        {conflictNotice && <p aria-live="polite">{t('edit.conflictRetry')}</p>}

        {save.error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(save.error) ? `errors.codes.${save.error.code}` : 'errors.generic', {
              defaultValue: t('errors.generic'),
            })}
          </StatusBanner>
        )}
      </div>

      {form && (
        <form noValidate onSubmit={handleSubmit}>
          {Object.keys(fieldErrors).length > 0 && (
            <div className="mx-auto max-w-[880px] px-3 sm:px-6">
              <ValidationSummary errors={fieldErrors} fieldIds={fieldIds} summaryRef={summaryRef} t={t} />
            </div>
          )}

          <div className="mx-auto max-w-[880px] space-y-4 px-3 py-4 sm:px-6">
            <Section heading={t('edit.heading')} icon={Gamepad2}>
              <SessionDetailsFieldset
                errors={fieldErrors}
                notesDisabled={form.clearNotes}
                showPlatform={false}
                t={t}
                values={form}
                onDurationChange={(value) => update('durationMinutes', value)}
                onNotesChange={(value) => update('notes', value)}
                onTitleChange={(value) => update('title', value)}
              />
              <label className={controlRowClasses}>
                <input
                  checked={form.clearNotes}
                  type="checkbox"
                  onChange={(event) => update('clearNotes', event.target.checked)}
                />
                {t('edit.clearNotes')}
              </label>

              <SessionGameField
                clearControl={
                  <label className={controlRowClasses}>
                    <input
                      checked={form.clearGame}
                      type="checkbox"
                      onChange={(event) => update('clearGame', event.target.checked)}
                    />
                    {t('edit.clearGame')}
                  </label>
                }
                disabled={form.clearGame}
                errors={fieldErrors}
                t={t}
                values={form}
                onGameAppIdChange={(appId) => update('gameAppId', appId)}
                onGameNameChange={(name) => update('gameName', name)}
                onGameSelect={({ name, appId }) => {
                  setForm((current) =>
                    current ? { ...current, gameName: name, gameAppId: appId } : current,
                  )
                  setDirty(true)
                }}
              />
            </Section>

            <Section heading={t('create.start')} icon={CalendarClock}>
              {locked ? (
                <p>{t('edit.startLocked')}</p>
              ) : (
                <SessionStartField t={t} value={form.start} onChange={(value) => update('start', value)} />
              )}
            </Section>

            <Section heading={t('create.min')} icon={Users}>
              <SessionAttendanceFieldset
                errors={fieldErrors}
                minLocked={locked}
                minLockedMessage={t('edit.minLocked')}
                t={t}
                unlimitedLabel={t('edit.clearMax')}
                values={{
                  minAttendance: form.minAttendance,
                  maxCapacity: form.maxCapacity,
                  unlimited: form.clearMax,
                }}
                onMaxChange={(value) => update('maxCapacity', value)}
                onMinChange={(value) => update('minAttendance', value)}
                onUnlimitedChange={(checked) => update('clearMax', checked)}
              />

              <div id="field-visibility">
                <VisibilityControl
                  groups={targetableGroups}
                  selectedGroupIds={form.audienceGroupIds}
                  value={form.visibility}
                  onChange={(visibility) => {
                    update('visibility', visibility)
                    update('visibilityTouched', true)
                  }}
                  onToggleGroup={(groupId) => {
                    update(
                      'audienceGroupIds',
                      form.audienceGroupIds.includes(groupId)
                        ? form.audienceGroupIds.filter((id) => id !== groupId)
                        : [...form.audienceGroupIds, groupId],
                    )
                    update('visibilityTouched', true)
                  }}
                />
                {fieldErrors.audienceGroupIds && (
                  <p role="alert">{fieldErrors.audienceGroupIds}</p>
                )}
                <p className="font-body text-meta text-ink-soft">{t('edit.privacyDisclosure')}</p>
              </div>
            </Section>
          </div>

          <div className="fixed inset-x-0 bottom-0 z-40 border-t border-border bg-card/95 backdrop-blur">
            <div className="mx-auto flex max-w-[880px] items-center justify-end gap-2 px-3 py-3 sm:px-6">
              <button
                ref={cancelTrigger}
                className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
                onClick={requestCancel}
              >
                {t('dirty.stay')}
              </button>
              <Button aria-busy={save.isPending} disabled={save.isPending} type="submit" variant="primary">
                {t('edit.save')}
              </Button>
            </div>
          </div>
        </form>
      )}

      <SessionDiscardDialog
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
