import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  controlRowClasses,
  fieldControlClasses,
  fieldSelectClasses,
  raisedInteractiveClasses,
} from '../../components/ui/classes'
import { DateEcho } from '../../components/ui/dateEcho'
import { Dialog } from '../../components/ui/dialog'
import { FormField, FormShell } from '../../components/ui/formShell'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
import { useDirtyGuard } from '../../components/useDirtyGuard'
import { friendKeys, getFriends } from '../friends/friendGraph'
import { createSession, type CreateSessionRequest } from './sessionQueries'
import { SteamGamePicker } from './SteamGamePicker'

type DiscardDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onDiscard: () => void
  triggerRef: React.RefObject<HTMLElement | null>
}

export function DiscardDialog({
  open,
  onOpenChange,
  onDiscard,
  triggerRef,
}: DiscardDialogProps) {
  const { t } = useTranslation('sessions')
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="discard-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('dirty.title')}
          </Dialog.Title>
          <Dialog.Description
            id="discard-dialog-description"
            className="mt-2 text-ink-soft"
          >
            {t('dirty.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button type="button" variant="danger" onClick={onDiscard}>
              {t('dirty.discard')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

// Single source of truth for the validation-summary link targets: every
// entry in `fieldErrors` must resolve to a real element id (H2).
const fieldIds: Record<string, string> = {
  title: 'field-title',
  start: 'field-start',
  durationMinutes: 'field-duration',
  minAttendance: 'field-min',
  maxCapacity: 'field-max',
  inviteeUserIds: 'field-invitees',
  guestLinkCount: 'field-guest-links',
  gameAppId: 'field-game-app-id',
  gameName: 'field-game-name',
}

const platformWireValues: Record<string, string> = {
  Steam: 'steam',
  PlayStation: 'playStation',
  Xbox: 'xbox',
  Other: 'notApplicable',
}

const emptyForm = {
  title: '',
  start: '',
  durationMinutes: '120',
  platform: '',
  gameName: '',
  gameAppId: '',
  notes: '',
  minAttendance: '4',
  maxCapacity: '4',
  unlimited: false,
  isPrivate: false,
  inviteeUserIds: [] as string[],
  guestLinkCount: '0',
}

// D-130's minimum lead, mirrored client-side (`RecommendationPrefill.MinimumLead`
// server-side): a prefilled start is raised to at least now + 15 min, never
// lowered — A-083's session-arm consumption half (design §10.1).
const MINIMUM_LEAD_MS = 15 * 60_000

function toDatetimeLocal(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(
    date.getHours(),
  )}:${pad(date.getMinutes())}`
}

type Prefill = {
  start: string
  durationMinutes: string | null
  startWasClamped: boolean
  overDurationMinutes: number
}

/**
 * Reads `/sessions/new?startTime&durationMinutes&overDurationMinutes`
 * (A-083, §10.1). S6 owns reading this URL; S7 writes it from a
 * recommendation. The clamp is re-applied here (not just trusted from the
 * URL) because time may have passed between the link being minted and this
 * page loading.
 */
function readPrefill(params: URLSearchParams): Prefill | null {
  const startTimeParam = params.get('startTime')
  if (!startTimeParam) return null

  const requested = new Date(startTimeParam)
  if (Number.isNaN(requested.getTime())) return null

  const earliest = new Date(Date.now() + MINIMUM_LEAD_MS)
  const startWasClamped = requested.getTime() < earliest.getTime()
  const effectiveStart = startWasClamped ? earliest : requested

  const durationParam = params.get('durationMinutes')
  const durationValue = durationParam ? Number(durationParam) : NaN
  const durationMinutes =
    Number.isFinite(durationValue) && durationValue > 0 ? String(durationValue) : null

  const overDurationParam = params.get('overDurationMinutes')
  const overDurationValue = overDurationParam ? Number(overDurationParam) : NaN
  const overDurationMinutes = Number.isFinite(overDurationValue) ? overDurationValue : 0

  return {
    start: toDatetimeLocal(effectiveStart),
    durationMinutes,
    startWasClamped,
    overDurationMinutes,
  }
}

export function SessionCreatePage() {
  const { t } = useTranslation(['sessions', 'errors', 'availability'])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const summaryRef = useRef<HTMLDivElement>(null)
  const cancelTrigger = useRef<HTMLButtonElement>(null)
  const [searchParams] = useSearchParams()
  const [prefill] = useState(() => readPrefill(searchParams))
  const [form, setForm] = useState(() =>
    prefill
      ? {
          ...emptyForm,
          start: prefill.start,
          durationMinutes: prefill.durationMinutes ?? emptyForm.durationMinutes,
        }
      : emptyForm,
  )
  const [dirty, setDirty] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [discardOpen, setDiscardOpen] = useState(false)
  const [pendingNavigation, setPendingNavigation] = useState<string | null>(null)

  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })

  const submit = useMutation({
    mutationFn: (body: CreateSessionRequest) => createSession(body),
    onSuccess: async (response) => {
      await queryClient.invalidateQueries({ queryKey: ['sessions'] })
      navigate(`/sessions/${response.session.id}`)
    },
  })

  useDirtyGuard(dirty, {
    onBlockedNavigation: (target) => {
      setPendingNavigation(target)
      setDiscardOpen(true)
    },
  })

  useEffect(() => {
    if (submit.error && summaryRef.current) {
      summaryRef.current.focus()
    }
  }, [submit.error])

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((current) => ({ ...current, [key]: value }))
    setDirty(true)
  }

  function validate(): Record<string, string> {
    const errors: Record<string, string> = {}
    if (!form.title.trim()) errors.title = t('validation.required')
    if (!form.start) errors.start = t('validation.required')
    const duration = Number(form.durationMinutes)
    if (!Number.isFinite(duration) || duration <= 0 || duration % 15 !== 0) {
      errors.durationMinutes = t('validation.increment')
    }
    const min = Number(form.minAttendance)
    const max = form.unlimited ? undefined : Number(form.maxCapacity)
    if (!Number.isFinite(min) || min < 1) errors.minAttendance = t('validation.range')
    if (!form.unlimited && (!Number.isFinite(max) || (max as number) < 1)) {
      errors.maxCapacity = t('validation.range')
    }
    if (!form.unlimited && max !== undefined && min > max) {
      errors.minAttendance = t('validation.minMax')
    }
    if (!form.isPrivate && form.inviteeUserIds.length === 0) {
      errors.inviteeUserIds = t('validation.invitedFriend')
    }
    if (form.inviteeUserIds.length > 50) {
      errors.inviteeUserIds = t('validation.tooManyInvitees')
    }
    const guestLinkCount = Number(form.guestLinkCount)
    if (guestLinkCount > 20) {
      errors.guestLinkCount = t('validation.tooManyGuestLinks')
    }
    if (form.platform === 'Steam' && form.gameAppId && !/^\d+$/.test(form.gameAppId)) {
      errors.gameAppId = t('validation.steamAppIdDigits')
    }
    if (form.platform === 'Steam' && form.gameAppId && !form.gameName.trim()) {
      errors.gameName = t('validation.steamAppIdNeedsGameName')
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

    const game = form.gameName
      ? {
          name: form.gameName,
          steamAppId:
            form.platform === 'Steam' && form.gameAppId ? form.gameAppId : null,
        }
      : null

    submit.mutate({
      title: form.title,
      startTime: new Date(form.start).toISOString(),
      durationMinutes: Number(form.durationMinutes),
      platform: form.platform ? platformWireValues[form.platform] : null,
      game,
      notes: form.notes || null,
      minAttendance: Number(form.minAttendance),
      maxCapacity: form.unlimited ? null : Number(form.maxCapacity),
      isPrivate: form.isPrivate,
      inviteeUserIds: form.inviteeUserIds.length > 0 ? form.inviteeUserIds : null,
      guestLinkCount: guestLinkCount(form.guestLinkCount),
    })
  }

  function guestLinkCount(value: string) {
    const parsed = Number(value)
    return parsed > 0 ? parsed : null
  }

  function requestCancel() {
    if (dirty) {
      setPendingNavigation('/sessions/upcoming')
      setDiscardOpen(true)
      return
    }
    navigate('/sessions/upcoming')
  }

  return (
    <main data-testid="screen-session-create">
      <PageScaffold width="form">
        <PageHeader title={t('create.heading')} />

        {prefill?.startWasClamped && (
          <p aria-live="polite" role="status">
            {t('prefill.clamped', { ns: 'availability' })}
          </p>
        )}

        {prefill && prefill.overDurationMinutes > 0 && (
          <p role="status">
            {t('prefill.overDuration', {
              ns: 'availability',
              minutes: prefill.overDurationMinutes,
            })}
          </p>
        )}

        {submit.error && (
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(submit.error)
                ? `errors.codes.${submit.error.code}`
                : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

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
              aria-busy={submit.isPending}
              disabled={submit.isPending}
              type="submit"
              variant="primary"
            >
              {t('create.submit')}
            </Button>
          }
          onSubmit={handleSubmit}
        >
          <FormField htmlFor="field-title" label={t('create.title')}>
            <input
              aria-describedby={fieldErrors.title ? 'title-error' : undefined}
              aria-invalid={Boolean(fieldErrors.title)}
              className={fieldControlClasses}
              id="field-title"
              required
              value={form.title}
              onChange={(event) => update('title', event.target.value)}
            />
            {fieldErrors.title && (
              <p id="title-error" role="alert">
                {fieldErrors.title}
              </p>
            )}
          </FormField>

          <FormField htmlFor="field-start" label={t('create.start')}>
            <input
              aria-describedby={fieldErrors.start ? 'start-error' : undefined}
              aria-invalid={Boolean(fieldErrors.start)}
              className={fieldControlClasses}
              id="field-start"
              step={900}
              type="datetime-local"
              value={form.start}
              onChange={(event) => update('start', event.target.value)}
            />
            <DateEcho value={form.start} />
            {fieldErrors.start && (
              <p id="start-error" role="alert">
                {fieldErrors.start}
              </p>
            )}
          </FormField>

          <FormField htmlFor="field-duration" label={t('create.duration')}>
            <input
              aria-describedby={fieldErrors.durationMinutes ? 'duration-error' : undefined}
              aria-invalid={Boolean(fieldErrors.durationMinutes)}
              className={fieldControlClasses}
              id="field-duration"
              min={15}
              step={15}
              type="number"
              value={form.durationMinutes}
              onChange={(event) => update('durationMinutes', event.target.value)}
            />
            {fieldErrors.durationMinutes && (
              <p id="duration-error" role="alert">
                {fieldErrors.durationMinutes}
              </p>
            )}
          </FormField>

          <FormField htmlFor="field-platform" label={t('create.platform')}>
            <select
              className={fieldSelectClasses}
              id="field-platform"
              value={form.platform}
              onChange={(event) => update('platform', event.target.value)}
            >
              <option value="">—</option>
              <option value="Steam">Steam</option>
              <option value="PlayStation">PlayStation</option>
              <option value="Xbox">Xbox</option>
              <option value="Other">{t('create.platformOther')}</option>
            </select>
          </FormField>

          <fieldset className="flex flex-col gap-1.5">
            <legend className="font-body font-semibold text-ink">{t('create.game')}</legend>
            {form.platform === 'Steam' ? (
              <SteamGamePicker
                appIdError={fieldErrors.gameAppId}
                appIdId="field-game-app-id"
                appIdValue={form.gameAppId}
                nameError={fieldErrors.gameName}
                nameId="field-game-name"
                nameValue={form.gameName}
                t={t}
                onAppIdChange={(appId) => update('gameAppId', appId)}
                onNameChange={(name) => update('gameName', name)}
                onSelect={({ name, appId }) => {
                  setForm((current) => ({ ...current, gameName: name, gameAppId: appId }))
                  setDirty(true)
                }}
              />
            ) : (
              <>
                <label className="mt-2 block" htmlFor="field-game-name">
                  {t('create.gameFreeText')}
                </label>
                <input
                  aria-describedby={fieldErrors.gameName ? 'game-name-error' : undefined}
                  aria-invalid={Boolean(fieldErrors.gameName)}
                  className={fieldControlClasses}
                  id="field-game-name"
                  value={form.gameName}
                  onChange={(event) => update('gameName', event.target.value)}
                />
                {fieldErrors.gameName && (
                  <p id="game-name-error" role="alert">
                    {fieldErrors.gameName}
                  </p>
                )}
              </>
            )}
          </fieldset>

          <FormField htmlFor="field-notes" label={t('create.notes')}>
            <textarea
              className={fieldControlClasses}
              id="field-notes"
              value={form.notes}
              onChange={(event) => update('notes', event.target.value)}
            />
          </FormField>

          <div className="grid gap-6 sm:grid-cols-2">
            <FormField
              hint={t('create.defaults')}
              htmlFor="field-min"
              label={t('create.min')}
            >
              <input
                aria-describedby={fieldErrors.minAttendance ? 'min-error' : 'field-min-hint'}
                aria-invalid={Boolean(fieldErrors.minAttendance)}
                className={fieldControlClasses}
                id="field-min"
                min={1}
                type="number"
                value={form.minAttendance}
                onChange={(event) => update('minAttendance', event.target.value)}
              />
              {fieldErrors.minAttendance && (
                <p id="min-error" role="alert">
                  {fieldErrors.minAttendance}
                </p>
              )}
            </FormField>
            <FormField htmlFor="field-max" label={t('create.max')}>
              <input
                aria-describedby={fieldErrors.maxCapacity ? 'max-error' : undefined}
                aria-invalid={Boolean(fieldErrors.maxCapacity)}
                className={fieldControlClasses}
                disabled={form.unlimited}
                id="field-max"
                min={1}
                type="number"
                value={form.maxCapacity}
                onChange={(event) => update('maxCapacity', event.target.value)}
              />
              {fieldErrors.maxCapacity && (
                <p id="max-error" role="alert">
                  {fieldErrors.maxCapacity}
                </p>
              )}
              <label className={controlRowClasses}>
                <input
                  checked={form.unlimited}
                  type="checkbox"
                  onChange={(event) => update('unlimited', event.target.checked)}
                />
                {t('create.unlimited')}
              </label>
            </FormField>
          </div>

          <label className={controlRowClasses}>
            <input
              checked={form.isPrivate}
              type="checkbox"
              onChange={(event) => update('isPrivate', event.target.checked)}
            />
            {t('create.private')}
          </label>

          <fieldset className="flex flex-col gap-2" id="field-invitees">
            <legend className="font-body font-semibold text-ink">
              {t('create.invitees')}
            </legend>
            {fieldErrors.inviteeUserIds && <p role="alert">{fieldErrors.inviteeUserIds}</p>}
            <ul className="flex flex-col gap-2">
              {(friends.data ?? []).map((friend) => (
                <li key={friend.friendshipId}>
                  <label className={controlRowClasses}>
                    <input
                      checked={form.inviteeUserIds.includes(friend.userId)}
                      type="checkbox"
                      onChange={(event) => {
                        const checked = event.target.checked
                        update(
                          'inviteeUserIds',
                          checked
                            ? [...form.inviteeUserIds, friend.userId]
                            : form.inviteeUserIds.filter((id) => id !== friend.userId),
                        )
                      }}
                    />
                    {friend.displayName} (@{friend.username})
                  </label>
                </li>
              ))}
            </ul>
          </fieldset>

          <FormField htmlFor="field-guest-links" label={t('create.guestLinks')}>
            <input
              aria-describedby={fieldErrors.guestLinkCount ? 'guest-links-error' : undefined}
              className={fieldControlClasses}
              id="field-guest-links"
              max={20}
              min={0}
              type="number"
              value={form.guestLinkCount}
              onChange={(event) => update('guestLinkCount', event.target.value)}
            />
            {fieldErrors.guestLinkCount && (
              <p id="guest-links-error" role="alert">
                {fieldErrors.guestLinkCount}
              </p>
            )}
          </FormField>
        </FormShell>

        <DiscardDialog
          open={discardOpen}
          triggerRef={cancelTrigger}
          onDiscard={() => {
            setDiscardOpen(false)
            setDirty(false)
            const target = pendingNavigation ?? '/sessions/upcoming'
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

