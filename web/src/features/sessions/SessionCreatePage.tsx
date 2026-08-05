import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { friendKeys, getFriends } from '../friends/friendGraph'
import {
  createSession,
  useDirtyGuard,
  type CreateSessionRequest,
} from './sessionQueries'

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
        <Dialog.Overlay className="fixed inset-0 bg-slate-950/40" />
        <Dialog.Content
          aria-describedby="discard-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-lg font-semibold text-slate-950">
            {t('dirty.title')}
          </Dialog.Title>
          <Dialog.Description
            id="discard-dialog-description"
            className="mt-2 text-sm text-slate-700"
          >
            {t('dirty.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <button
              className="min-h-11 rounded-md bg-red-700 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700"
              type="button"
              onClick={onDiscard}
            >
              {t('dirty.discard')}
            </button>
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
}

const emptyForm = {
  title: '',
  start: '',
  durationMinutes: '120',
  platform: '',
  gameSource: 'freeText' as 'catalog' | 'freeText',
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

export function SessionCreatePage() {
  const { t } = useTranslation(['sessions', 'errors'])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const summaryRef = useRef<HTMLDivElement>(null)
  const cancelTrigger = useRef<HTMLButtonElement>(null)
  const [form, setForm] = useState(emptyForm)
  const [dirty, setDirty] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [discardOpen, setDiscardOpen] = useState(false)

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

  useEffect(() => {
    heading.current?.focus()
  }, [])

  useDirtyGuard(dirty)

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

    const game =
      form.platform === 'Steam' && form.gameSource === 'catalog'
        ? {
            name: form.gameName,
            steamAppId: form.gameAppId ? form.gameAppId : null,
          }
        : form.gameName
          ? { name: form.gameName, steamAppId: null }
          : null

    submit.mutate({
      title: form.title,
      startTime: new Date(form.start).toISOString(),
      durationMinutes: Number(form.durationMinutes),
      platform: form.platform || null,
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
      setDiscardOpen(true)
      return
    }
    navigate('/sessions/upcoming')
  }

  return (
    <main
      className="mx-auto max-w-2xl space-y-6 p-4 sm:p-8"
      data-testid="screen-session-create"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {t('create.heading')}
      </h1>
      <p className="text-sm text-slate-600">{t('create.defaults')}</p>

      {Object.keys(fieldErrors).length > 0 && (
        <div
          ref={summaryRef}
          className="rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
          tabIndex={-1}
        >
          <p className="font-semibold text-red-900">
            {t('a11y.validationSummary')}
          </p>
          <ul className="mt-2 list-disc pl-5 text-sm text-red-800">
            {Object.entries(fieldErrors).map(([field, message]) => (
              <li key={field}>
                <a href={`#${fieldIds[field] ?? `field-${field}`}`}>{message}</a>
              </li>
            ))}
          </ul>
        </div>
      )}

      {submit.error && (
        <p role="alert">
          {t(
            isApiError(submit.error)
              ? `errors.codes.${submit.error.code}`
              : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      <form className="space-y-5" noValidate onSubmit={handleSubmit}>
        <div>
          <label className="block font-medium text-slate-900" htmlFor="field-title">
            {t('create.title')}
          </label>
          <input
            aria-describedby={fieldErrors.title ? 'title-error' : undefined}
            aria-invalid={Boolean(fieldErrors.title)}
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
        </div>

        <div>
          <label className="block font-medium text-slate-900" htmlFor="field-start">
            {t('create.start')}
          </label>
          <input
            aria-describedby={fieldErrors.start ? 'start-error' : undefined}
            aria-invalid={Boolean(fieldErrors.start)}
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
            id="field-start"
            step={900}
            type="datetime-local"
            value={form.start}
            onChange={(event) => update('start', event.target.value)}
          />
          {fieldErrors.start && (
            <p id="start-error" role="alert">
              {fieldErrors.start}
            </p>
          )}
        </div>

        <div>
          <label
            className="block font-medium text-slate-900"
            htmlFor="field-duration"
          >
            {t('create.duration')}
          </label>
          <input
            aria-describedby={
              fieldErrors.durationMinutes ? 'duration-error' : undefined
            }
            aria-invalid={Boolean(fieldErrors.durationMinutes)}
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
        </div>

        <div>
          <label
            className="block font-medium text-slate-900"
            htmlFor="field-platform"
          >
            {t('create.platform')}
          </label>
          <select
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
        </div>

        <fieldset>
          <legend className="font-medium text-slate-900">
            {t('create.game')}
          </legend>
          {form.platform === 'Steam' && (
            <div className="mt-1 flex gap-4">
              <label className="inline-flex min-h-11 items-center gap-2">
                <input
                  checked={form.gameSource === 'catalog'}
                  name="game-source"
                  type="radio"
                  onChange={() => update('gameSource', 'catalog')}
                />
                {t('create.gameCatalog')}
              </label>
              <label className="inline-flex min-h-11 items-center gap-2">
                <input
                  checked={form.gameSource === 'freeText'}
                  name="game-source"
                  type="radio"
                  onChange={() => update('gameSource', 'freeText')}
                />
                {t('create.gameFreeText')}
              </label>
            </div>
          )}
          <label className="mt-2 block" htmlFor="field-game-name">
            {t('create.gameFreeText')}
          </label>
          <input
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
            id="field-game-name"
            value={form.gameName}
            onChange={(event) => update('gameName', event.target.value)}
          />
          {form.platform === 'Steam' && form.gameSource === 'catalog' && (
            <input
              aria-label={t('create.steamAppId')}
              className="mt-2 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
              value={form.gameAppId}
              onChange={(event) => update('gameAppId', event.target.value)}
            />
          )}
        </fieldset>

        <div>
          <label
            className="block font-medium text-slate-900"
            htmlFor="field-notes"
          >
            {t('create.notes')}
          </label>
          <textarea
            className="mt-1 w-full rounded-md border border-slate-300 px-3 py-2"
            id="field-notes"
            value={form.notes}
            onChange={(event) => update('notes', event.target.value)}
          />
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <label className="block font-medium text-slate-900" htmlFor="field-min">
              {t('create.min')}
            </label>
            <input
              aria-describedby={
                fieldErrors.minAttendance ? 'min-error' : undefined
              }
              aria-invalid={Boolean(fieldErrors.minAttendance)}
              className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
          </div>
          <div>
            <label className="block font-medium text-slate-900" htmlFor="field-max">
              {t('create.max')}
            </label>
            <input
              aria-describedby={fieldErrors.maxCapacity ? 'max-error' : undefined}
              aria-invalid={Boolean(fieldErrors.maxCapacity)}
              className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2 disabled:bg-slate-100"
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
            <label className="mt-2 inline-flex min-h-11 items-center gap-2">
              <input
                checked={form.unlimited}
                type="checkbox"
                onChange={(event) => update('unlimited', event.target.checked)}
              />
              {t('create.unlimited')}
            </label>
          </div>
        </div>

        <label className="inline-flex min-h-11 items-center gap-2">
          <input
            checked={form.isPrivate}
            type="checkbox"
            onChange={(event) => update('isPrivate', event.target.checked)}
          />
          {t('create.private')}
        </label>

        <fieldset id="field-invitees">
          <legend className="font-medium text-slate-900">
            {t('create.invitees')}
          </legend>
          {fieldErrors.inviteeUserIds && (
            <p role="alert">{fieldErrors.inviteeUserIds}</p>
          )}
          <ul className="mt-2 space-y-1">
            {(friends.data ?? []).map((friend) => (
              <li key={friend.friendshipId}>
                <label className="inline-flex min-h-11 items-center gap-2">
                  <input
                    checked={form.inviteeUserIds.includes(friend.userId)}
                    type="checkbox"
                    onChange={(event) => {
                      const checked = event.target.checked
                      update(
                        'inviteeUserIds',
                        checked
                          ? [...form.inviteeUserIds, friend.userId]
                          : form.inviteeUserIds.filter(
                              (id) => id !== friend.userId,
                            ),
                      )
                    }}
                  />
                  {friend.displayName} (@{friend.username})
                </label>
              </li>
            ))}
          </ul>
        </fieldset>

        <div>
          <label
            className="block font-medium text-slate-900"
            htmlFor="field-guest-links"
          >
            {t('create.guestLinks')}
          </label>
          <input
            aria-describedby={
              fieldErrors.guestLinkCount ? 'guest-links-error' : undefined
            }
            className="mt-1 min-h-11 w-full rounded-md border border-slate-300 px-3 py-2"
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
        </div>

        <div className="flex flex-wrap justify-end gap-2">
          <button
            ref={cancelTrigger}
            className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            type="button"
            onClick={requestCancel}
          >
            {t('dirty.stay')}
          </button>
          <Button
            aria-busy={submit.isPending}
            className="min-h-11 rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
            disabled={submit.isPending}
            type="submit"
          >
            {t('create.submit')}
          </Button>
        </div>
      </form>

      <DiscardDialog
        open={discardOpen}
        triggerRef={cancelTrigger}
        onDiscard={() => {
          setDiscardOpen(false)
          setDirty(false)
          navigate('/sessions/upcoming')
        }}
        onOpenChange={setDiscardOpen}
      />
    </main>
  )
}
