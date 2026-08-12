import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { useDirtyGuard } from '../../components/useDirtyGuard'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { friendKeys, getFriends } from '../friends/friendGraph'
import { SteamGamePicker } from '../sessions/SteamGamePicker'
import { createPoll, type CreatePollRequest } from './pollQueries'

// D-080/D-131 (creation-time cap): the same 12-option ceiling `PollCreateTests`
// enforces server-side (`CreatePoll_DedupesIdenticalInstants_AndRejectsBelowMinimum_D080_D131`)
// — checked client-side too, purely for the inline UX; the server re-checks.
const MAX_OPTIONS = 12
const MIN_OPTIONS = 2

// D-130's minimum lead, mirrored client-side (`RecommendationPrefill.MinimumLead`
// server-side, matching `SessionCreatePage`'s own constant): a prefilled option instant
// is raised to at least now + 15 min, never lowered.
const MINIMUM_LEAD_MS = 15 * 60_000

/**
 * A-083's poll-arm consumption half (design §5): reads the repeated `startTimes` query
 * param FindATimePage writes, clamping each instant the same way
 * `RecommendationPrefill.ForPollOption` does server-side (never lowering, never widening) and
 * deduping (D-080's merge, not a validation error) up to the 12-option ceiling (§12-11's
 * server-side re-validated cap — this is client UX only). The clamp is re-applied here, not just
 * trusted from the URL, because time may have passed between the link being minted and this page
 * loading. Returns the deduped ISO instants (truncated to the cap, never silently — `anyTruncated`
 * says so) plus whether any one of them was clamped, surfaced once rather than per-option (clamp
 * surfaced not silent, never silent — the same discipline applies to truncation).
 */
function readPollPrefill(
  params: URLSearchParams,
): { options: string[]; anyClamped: boolean; anyTruncated: boolean } {
  const raw = params.getAll('startTimes')
  if (raw.length === 0) return { options: [], anyClamped: false, anyTruncated: false }

  const earliest = Date.now() + MINIMUM_LEAD_MS
  const deduped: string[] = []
  let anyClamped = false

  for (const value of raw) {
    const requested = new Date(value)
    if (Number.isNaN(requested.getTime())) continue
    const clamped = requested.getTime() < earliest
    if (clamped) anyClamped = true
    const iso = new Date(clamped ? earliest : requested.getTime()).toISOString()
    if (!deduped.includes(iso)) {
      deduped.push(iso)
    }
  }

  const sorted = deduped.sort()
  return {
    options: sorted.slice(0, MAX_OPTIONS),
    anyClamped,
    anyTruncated: sorted.length > MAX_OPTIONS,
  }
}

type DiscardDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onDiscard: () => void
  triggerRef: React.RefObject<HTMLElement | null>
}

function PollDiscardDialog({
  open,
  onOpenChange,
  onDiscard,
  triggerRef,
}: DiscardDialogProps) {
  const { t } = useTranslation('polls')
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="poll-discard-dialog-description"
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
            id="poll-discard-dialog-description"
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

const fieldIds: Record<string, string> = {
  title: 'field-title',
  options: 'field-options',
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

const deadlineChoices = ['hours6', 'hours12', 'hours24', 'hours48'] as const

const emptyForm = {
  title: '',
  optionValue: '',
  options: [] as string[],
  deadlineChoice: 'hours24' as (typeof deadlineChoices)[number],
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

export function PollCreatePage() {
  const { t } = useTranslation(['polls', 'errors', 'availability'])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const summaryRef = useRef<HTMLDivElement>(null)
  const cancelTrigger = useRef<HTMLButtonElement>(null)
  const [searchParams] = useSearchParams()
  const [prefill] = useState(() => readPollPrefill(searchParams))
  const [form, setForm] = useState(() =>
    prefill.options.length > 0 ? { ...emptyForm, options: prefill.options } : emptyForm,
  )
  const [dirty, setDirty] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [optionNotice, setOptionNotice] = useState<string | null>(null)
  const [discardOpen, setDiscardOpen] = useState(false)
  const [pendingNavigation, setPendingNavigation] = useState<string | null>(null)

  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })

  const submit = useMutation({
    mutationFn: (body: CreatePollRequest) => createPoll(body),
    onSuccess: async (response) => {
      await queryClient.invalidateQueries({ queryKey: ['polls'] })
      navigate(`/polls/${response.poll.id}`)
    },
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

  useEffect(() => {
    if (submit.error && summaryRef.current) {
      summaryRef.current.focus()
    }
  }, [submit.error])

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((current) => ({ ...current, [key]: value }))
    setDirty(true)
  }

  function addOption() {
    setOptionNotice(null)
    if (!form.optionValue) return
    const asInstant = new Date(form.optionValue)
    if (Number.isNaN(asInstant.getTime())) return
    const iso = asInstant.toISOString()

    // D-080: identical instants merge visibly, never a validation error.
    if (form.options.includes(iso)) {
      setOptionNotice(t('create.optionMerged'))
      setForm((current) => ({ ...current, optionValue: '' }))
      setDirty(true)
      return
    }

    if (form.options.length >= MAX_OPTIONS) {
      setFieldErrors((current) => ({ ...current, options: t('validation.optionCap') }))
      return
    }

    setFieldErrors((current) => {
      if (!('options' in current)) return current
      const rest = { ...current }
      delete rest.options
      return rest
    })
    setForm((current) => ({
      ...current,
      options: [...current.options, iso].sort(),
      optionValue: '',
    }))
    setDirty(true)
  }

  function removeOption(iso: string) {
    setForm((current) => ({
      ...current,
      options: current.options.filter((value) => value !== iso),
    }))
    setDirty(true)
  }

  function validate(): Record<string, string> {
    const errors: Record<string, string> = {}
    if (!form.title.trim()) errors.title = t('validation.required')
    if (form.options.length < MIN_OPTIONS) errors.options = t('validation.optionMinimum')
    if (form.options.length > MAX_OPTIONS) errors.options = t('validation.optionCap')
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
    // D-125: a non-private poll needs at least one directly invited registered friend.
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
      optionStartTimes: form.options,
      deadlineChoice: form.deadlineChoice,
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
      setPendingNavigation('/polls')
      setDiscardOpen(true)
      return
    }
    navigate('/polls')
  }

  return (
    <main
      className="mx-auto max-w-2xl space-y-6 p-4 sm:p-8"
      data-testid="screen-poll-create"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {t('create.heading')}
      </h1>
      <p className="text-ink-soft">{t('create.defaults')}</p>

      {prefill.anyClamped && (
        <p aria-live="polite" role="status">
          {t('prefill.clamped', { ns: 'availability' })}
        </p>
      )}

      {prefill.anyTruncated && (
        <p aria-live="polite" role="status">
          {t('prefill.pollTruncated', { max: MAX_OPTIONS })}
        </p>
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
          <label className="block font-body font-semibold text-ink" htmlFor="field-title">
            {t('create.title')}
          </label>
          <input
            aria-describedby={fieldErrors.title ? 'title-error' : undefined}
            aria-invalid={Boolean(fieldErrors.title)}
            className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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

        <fieldset id="field-options">
          <legend className="font-body font-semibold text-ink">
            {t('create.options')}
          </legend>
          <p className="text-ink-soft">{t('create.optionsHelp')}</p>
          <div className="mt-2 flex flex-wrap items-end gap-2">
            <div className="flex-1">
              <label className="sr-only" htmlFor="field-option-value">
                {t('create.addOption')}
              </label>
              <input
                className="min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                id="field-option-value"
                step={900}
                type="datetime-local"
                value={form.optionValue}
                onChange={(event) => update('optionValue', event.target.value)}
              />
            </div>
            <Button type="button" variant="neutral" onClick={addOption}>
              {t('create.addOption')}
            </Button>
          </div>
          {optionNotice && (
            <p aria-live="polite" className="mt-1 text-ink-soft" role="status">
              {optionNotice}
            </p>
          )}
          {fieldErrors.options && (
            <p id="options-error" role="alert">
              {fieldErrors.options}
            </p>
          )}
          {form.options.length > 0 && (
            <ul className="mt-3 space-y-1">
              {form.options.map((iso) => (
                <li
                  key={iso}
                  className="flex items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-2"
                >
                  <span className="text-ink">
                    {new Intl.DateTimeFormat(undefined, {
                      dateStyle: 'medium',
                      timeStyle: 'short',
                    }).format(new Date(iso))}
                  </span>
                  <Button
                    aria-label={`${t('create.removeOption')} ${new Intl.DateTimeFormat(
                      undefined,
                      { dateStyle: 'medium', timeStyle: 'short' },
                    ).format(new Date(iso))}`}
                    type="button"
                    variant="ghost"
                    onClick={() => removeOption(iso)}
                  >
                    {t('create.removeOption')}
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </fieldset>

        <div>
          <label
            className="block font-body font-semibold text-ink"
            htmlFor="field-deadline"
          >
            {t('create.deadline')}
          </label>
          <select
            className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            id="field-deadline"
            value={form.deadlineChoice}
            onChange={(event) =>
              update(
                'deadlineChoice',
                event.target.value as (typeof deadlineChoices)[number],
              )
            }
          >
            {deadlineChoices.map((choice) => (
              <option key={choice} value={choice}>
                {t(`create.deadlineChoice.${choice}`)}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label
            className="block font-body font-semibold text-ink"
            htmlFor="field-duration"
          >
            {t('create.duration')}
          </label>
          <input
            aria-describedby={
              fieldErrors.durationMinutes ? 'duration-error' : undefined
            }
            aria-invalid={Boolean(fieldErrors.durationMinutes)}
            className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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
            className="block font-body font-semibold text-ink"
            htmlFor="field-platform"
          >
            {t('create.platform')}
          </label>
          <select
            className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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
          <legend className="font-body font-semibold text-ink">
            {t('create.game')}
          </legend>
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
                className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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

        <div>
          <label
            className="block font-body font-semibold text-ink"
            htmlFor="field-notes"
          >
            {t('create.notes')}
          </label>
          <textarea
            className="mt-1 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            id="field-notes"
            value={form.notes}
            onChange={(event) => update('notes', event.target.value)}
          />
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <label className="block font-body font-semibold text-ink" htmlFor="field-min">
              {t('create.min')}
            </label>
            <input
              aria-describedby={
                fieldErrors.minAttendance ? 'min-error' : undefined
              }
              aria-invalid={Boolean(fieldErrors.minAttendance)}
              className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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
            <label className="block font-body font-semibold text-ink" htmlFor="field-max">
              {t('create.max')}
            </label>
            <input
              aria-describedby={fieldErrors.maxCapacity ? 'max-error' : undefined}
              aria-invalid={Boolean(fieldErrors.maxCapacity)}
              className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:bg-busy disabled:text-ink-soft disabled:opacity-100"
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
          <legend className="font-body font-semibold text-ink">
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
                  <AvatarDisc
                    emoji={friend.avatar.emoji}
                    imgUrl={avatarImgUrl(friend.avatar)}
                    seed={friend.userId}
                    size="xs"
                  />
                  {friend.displayName} (@{friend.username})
                </label>
              </li>
            ))}
          </ul>
        </fieldset>

        <div>
          <label
            className="block font-body font-semibold text-ink"
            htmlFor="field-guest-links"
          >
            {t('create.guestLinks')}
          </label>
          <input
            aria-describedby={
              fieldErrors.guestLinkCount ? 'guest-links-error' : undefined
            }
            className="mt-1 min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
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
            className={`px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
            type="button"
            onClick={requestCancel}
          >
            {t('dirty.stay')}
          </button>
          <Button
            aria-busy={submit.isPending}
            disabled={submit.isPending}
            type="submit"
            variant="primary"
          >
            {t('create.submit')}
          </Button>
        </div>
      </form>

      <PollDiscardDialog
        open={discardOpen}
        triggerRef={cancelTrigger}
        onDiscard={() => {
          setDiscardOpen(false)
          setDirty(false)
          const target = pendingNavigation ?? '/polls'
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
