import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { useSession } from '../../auth/useSession'
import {
  approveJoinRequest,
  cancelSession,
  getJoinRequests,
  getSessionDetail,
  getViewerProfile,
  invalidateSessionQueries,
  isSessionInProgress,
  leaveSession,
  promoteWaitlisted,
  rejectJoinRequest,
  rejectWaitlisted,
  removeGuest,
  requestJoin,
  respondToInvitation,
  sessionKeys,
  setReminder,
  viewerProfileKey,
  withdrawJoinRequest,
  type ParticipationResponse,
  type ReminderChoiceValue,
} from './sessionQueries'

const reminderOptions: { value: ReminderChoiceValue; key: string }[] = [
  { value: null, key: 'reminder.accountDefault' },
  { value: 'Off', key: 'reminder.off' },
  { value: 'Minutes15', key: 'reminder.minutes15' },
  { value: 'Minutes30', key: 'reminder.minutes30' },
  { value: 'Hours1', key: 'reminder.hours1' },
  { value: 'Hours3', key: 'reminder.hours3' },
  { value: 'Hours12', key: 'reminder.hours12' },
  { value: 'Hours24', key: 'reminder.hours24' },
]

type CancelDialogProps = {
  pending: boolean
  onConfirm: () => void
}

function CancelSessionDialog({ pending, onConfirm }: CancelDialogProps) {
  const { t } = useTranslation('sessions')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          className="min-h-11 rounded-md border border-red-300 px-4 py-2 text-red-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700"
          disabled={pending}
        >
          {t('cancel.action')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-slate-950/40" />
        <Dialog.Content
          aria-describedby="cancel-session-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-lg font-semibold text-slate-950">
            {t('cancel.title')}
          </Dialog.Title>
          <Dialog.Description
            id="cancel-session-description"
            className="mt-2 text-sm text-slate-700"
          >
            {t('cancel.body')}
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
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('cancel.confirm')}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type RemoveGuestDialogProps = {
  displayName: string
  pending: boolean
  onConfirm: () => void
}

function RemoveGuestDialog({
  displayName,
  pending,
  onConfirm,
}: RemoveGuestDialogProps) {
  const { t } = useTranslation('sessions')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-label={`${t('invite.removeGuest')} ${displayName}`}
          className="min-h-11 rounded-md border border-red-300 px-3 py-2 text-sm text-red-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700"
          disabled={pending}
        >
          {t('invite.removeGuest')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-slate-950/40" />
        <Dialog.Content
          aria-describedby="remove-guest-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-lg font-semibold text-slate-950">
            {t('invite.removeGuest')}
          </Dialog.Title>
          <Dialog.Description
            id="remove-guest-description"
            className="mt-2 text-sm text-slate-700"
          >
            {t('invite.removeGuestConfirm')}
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
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('invite.removeGuest')}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export function SessionDetailPage() {
  const { t, i18n } = useTranslation(['sessions', 'errors', 'common'])
  const { sessionId = '' } = useParams()
  const queryClient = useQueryClient()
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const { data: session } = useSession()

  const detail = useQuery({
    queryKey: sessionKeys.detail(sessionId),
    queryFn: () => getSessionDetail(sessionId),
    enabled: Boolean(sessionId),
    retry: false,
  })

  // D-038/D-046: times render in the viewer's profile time zone, not the
  // browser's, falling back to the browser zone only until the profile
  // query resolves.
  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const viewerTimeZone = profile.data?.timeZone ?? undefined

  const isOrganizer =
    Boolean(detail.data) && detail.data?.organizerId === session?.userId

  const joinRequests = useQuery({
    queryKey: sessionKeys.joinRequests(sessionId),
    queryFn: () => getJoinRequests(sessionId),
    enabled: Boolean(sessionId) && isOrganizer,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  async function afterMutation(message: string, options?: { removeDetail?: boolean }) {
    setStatus(message)
    await invalidateSessionQueries(queryClient, sessionId, options)
  }

  const respond = useMutation({
    mutationFn: (response: ParticipationResponse) =>
      respondToInvitation(sessionId, response),
    onSuccess: () => afterMutation(t('rsvp.updated')),
  })

  const leave = useMutation({
    mutationFn: () => leaveSession(sessionId),
    onSuccess: () => afterMutation(t('rsvp.updated')),
  })

  const join = useMutation({
    mutationFn: () => requestJoin(sessionId),
    onSuccess: () => afterMutation(t('join.received')),
  })

  const withdraw = useMutation({
    mutationFn: () => withdrawJoinRequest(sessionId),
    onSuccess: () => afterMutation(t('join.received')),
  })

  const approve = useMutation({
    mutationFn: (requestId: string) => approveJoinRequest(sessionId, requestId),
    onSuccess: () => afterMutation(t('join.accepted')),
  })

  const reject = useMutation({
    mutationFn: (requestId: string) => rejectJoinRequest(sessionId, requestId),
    onSuccess: () => afterMutation(t('join.rejected')),
  })

  const promote = useMutation({
    mutationFn: (participationId: string) =>
      promoteWaitlisted(sessionId, participationId),
    onSuccess: () => afterMutation(t('waitlist.promoted')),
  })

  const rejectWaitlist = useMutation({
    mutationFn: (participationId: string) =>
      rejectWaitlisted(sessionId, participationId),
    onSuccess: () => afterMutation(t('waitlist.vacancy')),
  })

  const removeGuestParticipant = useMutation({
    mutationFn: (participationId: string) => removeGuest(sessionId, participationId),
    onSuccess: () => afterMutation(t('invite.removeGuest')),
  })

  const cancel = useMutation({
    mutationFn: () => cancelSession(sessionId),
    onSuccess: () => afterMutation('', { removeDetail: true }),
  })

  const reminder = useMutation({
    mutationFn: (choice: ReminderChoiceValue) => setReminder(sessionId, choice),
    onSuccess: () => afterMutation(t('reminder.label')),
  })

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/welcome" />
  }

  if (cancel.isSuccess) {
    return <Navigate replace to="/sessions/upcoming" />
  }

  const anyError =
    detail.error ??
    respond.error ??
    leave.error ??
    join.error ??
    withdraw.error ??
    approve.error ??
    reject.error ??
    promote.error ??
    rejectWaitlist.error ??
    removeGuestParticipant.error ??
    cancel.error ??
    reminder.error

  const data = detail.data
  const going = data?.roster.filter((r) => r.status === 'going' && !r.isWaitlisted) ?? []
  const maybe = data?.roster.filter((r) => r.status === 'maybe' && !r.isWaitlisted) ?? []
  const declined = data?.roster.filter((r) => r.status === 'declined') ?? []
  const pending = data?.roster.filter((r) => r.status === 'pending') ?? []
  const waitlisted = data?.roster.filter((r) => r.isWaitlisted) ?? []
  const viewerEntry = data?.roster.find((r) => r.userId === session?.userId)
  const inProgress = data ? isSessionInProgress(data) : false

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-session-detail"
    >
      <h1
        ref={heading}
        aria-live="polite"
        tabIndex={-1}
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {data?.title ?? t('common.loading', { ns: 'common' })}
      </h1>

      <p aria-live="polite" className="text-sm text-emerald-700">
        {status}
      </p>

      {anyError && (
        <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
          {t(
            isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      {data && (
        <>
          <p>
            {new Intl.DateTimeFormat(i18n.language, {
              dateStyle: 'medium',
              timeStyle: 'short',
              timeZone: viewerTimeZone,
            }).format(new Date(data.startTime))}
            {inProgress && (
              <span className="ml-2 font-semibold text-emerald-700">
                {t('detail.inProgress')}
              </span>
            )}
          </p>
          {data.notes && <p>{data.notes}</p>}

          {isOrganizer && (
            <div className="flex flex-wrap gap-2">
              <Link
                className="min-h-11 inline-flex items-center rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                to={`/sessions/${sessionId}/edit`}
              >
                {t('edit.heading')}
              </Link>
              <Link
                className="min-h-11 inline-flex items-center rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                to={`/sessions/${sessionId}/invite`}
              >
                {t('invite.heading')}
              </Link>
              <CancelSessionDialog
                pending={cancel.isPending}
                onConfirm={() => cancel.mutate()}
              />
            </div>
          )}

          <section aria-labelledby="rsvp-heading">
            <h2 id="rsvp-heading" className="text-xl font-semibold text-slate-950">
              {t('rsvp.heading')}
            </h2>
            <div className="mt-2 flex flex-wrap gap-2">
              {(['Going', 'Maybe', 'Declined'] as const).map((choice) => (
                <button
                  key={choice}
                  aria-pressed={viewerEntry?.status === choice.toLowerCase()}
                  className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                  disabled={respond.isPending}
                  type="button"
                  onClick={() => respond.mutate(choice)}
                >
                  {t(`rsvp.${choice.toLowerCase()}`)}
                </button>
              ))}
              <button
                className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                disabled={leave.isPending}
                type="button"
                onClick={() => leave.mutate()}
              >
                {t('rsvp.leave')}
              </button>
            </div>

            <div className="mt-4 flex flex-wrap gap-2">
              <button
                className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                disabled={join.isPending}
                type="button"
                onClick={() => join.mutate()}
              >
                {t('join.request')}
              </button>
              <button
                className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                disabled={withdraw.isPending}
                type="button"
                onClick={() => withdraw.mutate()}
              >
                {t('join.withdraw')}
              </button>
            </div>

            <div className="mt-4">
              <label className="font-medium text-slate-900" htmlFor="reminder-select">
                {t('reminder.label')}
              </label>
              <select
                className="mt-1 min-h-11 rounded-md border border-slate-300 px-3 py-2"
                id="reminder-select"
                value={viewerEntry?.reminderChoice ?? ''}
                onChange={(event) =>
                  reminder.mutate(
                    (event.target.value || null) as ReminderChoiceValue,
                  )
                }
              >
                {reminderOptions.map((option) => (
                  <option key={option.key} value={option.value ?? ''}>
                    {t(option.key)}
                  </option>
                ))}
              </select>
            </div>
          </section>

          {isOrganizer && joinRequests.data && joinRequests.data.items.length > 0 && (
            <section aria-labelledby="join-requests-heading">
              <h2
                id="join-requests-heading"
                className="text-xl font-semibold text-slate-950"
              >
                {t('join.request')}
              </h2>
              <ul className="mt-2 space-y-2">
                {joinRequests.data.items.map((request) => (
                  <li
                    key={request.id}
                    className="flex items-center justify-between gap-2 rounded-md border border-slate-200 p-3"
                  >
                    <span>{request.requesterId}</span>
                    <span className="flex gap-2">
                      <button
                        className="min-h-11 rounded-md border border-slate-300 px-3 py-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                        type="button"
                        onClick={() => approve.mutate(request.id)}
                      >
                        {t('join.approve')}
                      </button>
                      <button
                        className="min-h-11 rounded-md border border-slate-300 px-3 py-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                        type="button"
                        onClick={() => reject.mutate(request.id)}
                      >
                        {t('join.reject')}
                      </button>
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section aria-label={t('a11y.rosterLabel')} aria-labelledby="roster-heading">
            <h2 id="roster-heading" className="text-xl font-semibold text-slate-950">
              {t('detail.roster')}
            </h2>
            {(
              [
                ['going', going],
                ['maybe', maybe],
                ['declined', declined],
                ['pending', pending],
              ] as const
            ).map(([key, entries]) => (
              <div key={key} className="mt-3">
                <h3 className="font-semibold text-slate-900">
                  {t(`detail.${key}`)} ({entries.length})
                </h3>
                <ul>
                  {entries.map((entry) => (
                    <li
                      key={entry.participationId}
                      className="flex items-center justify-between gap-2 py-1"
                    >
                      <span>{entry.displayName ?? t('detail.anonymous')}</span>
                      {isOrganizer && entry.origin === 'guestLink' && (
                        <RemoveGuestDialog
                          displayName={entry.displayName ?? t('detail.anonymous')}
                          pending={removeGuestParticipant.isPending}
                          onConfirm={() =>
                            removeGuestParticipant.mutate(entry.participationId)
                          }
                        />
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </section>

          {waitlisted.length > 0 && (
            <section aria-label={t('a11y.waitlistLabel')} aria-labelledby="waitlist-heading">
              <h2 id="waitlist-heading" className="text-xl font-semibold text-slate-950">
                {t('waitlist.heading')} ({waitlisted.length})
              </h2>
              <ul>
                {waitlisted.map((entry) => (
                  <li
                    key={entry.participationId}
                    className="flex items-center justify-between gap-2 py-1"
                  >
                    <span>{entry.displayName ?? t('detail.anonymous')}</span>
                    {isOrganizer && (
                      <span className="flex gap-2">
                        <button
                          className="min-h-11 rounded-md border border-slate-300 px-3 py-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                          type="button"
                          onClick={() => promote.mutate(entry.participationId)}
                        >
                          {t('waitlist.promote')}
                        </button>
                        <button
                          className="min-h-11 rounded-md border border-slate-300 px-3 py-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                          type="button"
                          onClick={() => rejectWaitlist.mutate(entry.participationId)}
                        >
                          {t('waitlist.reject')}
                        </button>
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}
    </main>
  )
}
