import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router'
import { CalendarClock, Gamepad2, Users } from 'lucide-react'
import { isApiError } from '../../../api/client'
import { Button } from '../../../components/ui/button'
import { raisedInteractiveClasses } from '../../../components/ui/classes'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { useDirtyGuard } from '../../../components/useDirtyGuard'
import { friendKeys, getFriends } from '../../friends/friendGraph'
import { createSession, type CreateSessionRequest } from '../sessionQueries'
import {
  Section,
  SessionAttendanceFieldset,
  SessionDetailsFieldset,
  SessionDiscardDialog,
  SessionGameField,
  SessionGuestLinksField,
  SessionInviteesFieldset,
  SessionPrivacyControl,
  SessionStartField,
  ValidationSummary,
} from './SessionFormBits'

/**
 * Behavior carried by this page: D-129 (fixed defaults), D-176/D-194 (Steam
 * catalog incl. degraded arm, via `SteamGamePicker`), D-152/D-162 (dirty
 * guard + discard dialog, via `useDirtyGuard`), D-102 (prefill from a
 * recommendation link, clamped to the minimum lead), D-109 (the
 * over-duration warning, non-blocking).
 */

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
 * (A-083, §10.1). The clamp is re-applied here (not just trusted from the
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
  const heading = useRef<HTMLHeadingElement>(null)
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

  // D-139: the page heading takes programmatic focus on mount.
  useEffect(() => {
    heading.current?.focus()
  }, [])

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
          steamAppId: form.platform === 'Steam' && form.gameAppId ? form.gameAppId : null,
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
      guestLinkCount: guestLinkCountOf(form.guestLinkCount),
    })
  }

  function guestLinkCountOf(value: string) {
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
    <main className="min-h-screen bg-background pb-32" data-testid="screen-session-create">
      <div className="mx-auto max-w-[880px] px-3 pt-5 sm:px-6">
        <h1
          ref={heading}
          className="font-display text-xl font-extrabold text-ink focus:outline-none sm:text-2xl"
          tabIndex={-1}
        >
          {t('create.heading')}
        </h1>
      </div>

      {prefill?.startWasClamped && (
        <p aria-live="polite" className="mx-auto max-w-[880px] px-3 sm:px-6" role="status">
          {t('prefill.clamped', { ns: 'availability' })}
        </p>
      )}

      {prefill && prefill.overDurationMinutes > 0 && (
        <p className="mx-auto max-w-[880px] px-3 sm:px-6" role="status">
          {t('prefill.overDuration', {
            ns: 'availability',
            minutes: prefill.overDurationMinutes,
          })}
        </p>
      )}

      {submit.error && (
        <div className="mx-auto max-w-[880px] px-3 sm:px-6">
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(submit.error) ? `errors.codes.${submit.error.code}` : 'errors.generic', {
              defaultValue: t('errors.generic'),
            })}
          </StatusBanner>
        </div>
      )}

      <form noValidate onSubmit={handleSubmit}>
        {Object.keys(fieldErrors).length > 0 && (
          <div className="mx-auto max-w-[880px] px-3 sm:px-6">
            <ValidationSummary errors={fieldErrors} fieldIds={fieldIds} summaryRef={summaryRef} t={t} />
          </div>
        )}

        <div className="mx-auto max-w-[880px] space-y-4 px-3 py-4 sm:px-6">
          <Section heading={t('create.heading')} icon={Gamepad2}>
            <SessionDetailsFieldset
              errors={fieldErrors}
              t={t}
              values={form}
              onDurationChange={(value) => update('durationMinutes', value)}
              onNotesChange={(value) => update('notes', value)}
              onPlatformChange={(value) => update('platform', value)}
              onTitleChange={(value) => update('title', value)}
            />
            <SessionGameField
              errors={fieldErrors}
              t={t}
              values={form}
              onGameAppIdChange={(appId) => update('gameAppId', appId)}
              onGameNameChange={(name) => update('gameName', name)}
              onGameSelect={({ name, appId }) => {
                setForm((current) => ({ ...current, gameName: name, gameAppId: appId }))
                setDirty(true)
              }}
            />
          </Section>

          <Section heading={t('create.start')} icon={CalendarClock}>
            <SessionStartField
              error={fieldErrors.start}
              t={t}
              value={form.start}
              onChange={(value) => update('start', value)}
            />
          </Section>

          <Section heading={t('create.invitees')} icon={Users}>
            <p className="font-body text-meta text-ink-soft">{t('create.defaults')}</p>
            <SessionAttendanceFieldset
              errors={fieldErrors}
              t={t}
              unlimitedLabel={t('create.unlimited')}
              values={form}
              onMaxChange={(value) => update('maxCapacity', value)}
              onMinChange={(value) => update('minAttendance', value)}
              onUnlimitedChange={(checked) => update('unlimited', checked)}
            />

            <SessionPrivacyControl
              t={t}
              value={form.isPrivate}
              onChange={(isPrivate) => update('isPrivate', isPrivate)}
            />

            <SessionInviteesFieldset
              error={fieldErrors.inviteeUserIds}
              friends={friends.data ?? []}
              selected={form.inviteeUserIds}
              t={t}
              onToggle={(userId, checked) =>
                update(
                  'inviteeUserIds',
                  checked
                    ? [...form.inviteeUserIds, userId]
                    : form.inviteeUserIds.filter((id) => id !== userId),
                )
              }
            />

            <SessionGuestLinksField
              error={fieldErrors.guestLinkCount}
              t={t}
              value={form.guestLinkCount}
              onChange={(value) => update('guestLinkCount', value)}
            />
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
            <Button aria-busy={submit.isPending} disabled={submit.isPending} type="submit" variant="primary">
              {t('create.submit')}
            </Button>
          </div>
        </div>
      </form>

      <SessionDiscardDialog
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
    </main>
  )
}
