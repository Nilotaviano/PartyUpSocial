import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { Button } from '../../components/ui/button'
import {
  chipClasses,
  quietChipClasses,
  raisedInteractiveClasses,
  segmentItemClasses,
} from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { IdentityRow } from '../../components/ui/identityRow'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { SegmentStrip } from '../../components/ui/segmentStrip'
import { useSession } from '../../auth/useSession'
import { SessionStatusChip } from './SessionStatusChip'
import {
  approveJoinRequest,
  cancelSession,
  getJoinRequests,
  getSessionDetail,
  getViewerProfile,
  invalidateSessionQueries,
  isSessionInProgress,
  leaveSession,
  muteSession,
  promoteWaitlisted,
  rejectJoinRequest,
  rejectWaitlisted,
  removeGuest,
  requestJoin,
  respondToInvitation,
  sessionKeys,
  setReminder,
  unmuteSession,
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
        <Button disabled={pending} variant="danger">
          {t('cancel.action')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="cancel-session-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('cancel.title')}
          </Dialog.Title>
          <Dialog.Description
            id="cancel-session-description"
            className="mt-2 text-ink-soft"
          >
            {t('cancel.body')}
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
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('cancel.confirm')}
            </Button>
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
          disabled={pending}
          variant="danger"
        >
          {t('invite.removeGuest')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="remove-guest-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('invite.removeGuest')}
          </Dialog.Title>
          <Dialog.Description
            id="remove-guest-description"
            className="mt-2 text-ink-soft"
          >
            {t('invite.removeGuestConfirm')}
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
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('invite.removeGuest')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

type RosterIdentityProps = {
  actions?: ReactNode
  avatar: ReactNode
  displayName: string
  masked: boolean
}

/**
 * A roster/waitlist person. The masked arm is a separate call rather than a
 * computed `variant` prop so the D-076 row keeps IdentityRow's own type-level
 * guarantee that no username can ride along with it.
 */
function RosterIdentity({ actions, avatar, displayName, masked }: RosterIdentityProps) {
  return masked ? (
    <IdentityRow actions={actions} avatar={avatar} displayName={displayName} variant="masked" />
  ) : (
    <IdentityRow actions={actions} avatar={avatar} displayName={displayName} variant="full" />
  )
}

export function SessionDetailPage() {
  const { t, i18n } = useTranslation(['sessions', 'errors', 'common'])
  const { sessionId = '' } = useParams()
  const queryClient = useQueryClient()
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

  const mute = useMutation({
    mutationFn: async () => {
      const wasMuted = detail.data?.muted ?? false
      if (wasMuted) {
        await unmuteSession(sessionId)
      } else {
        await muteSession(sessionId)
      }
      return !wasMuted
    },
    onSuccess: (nowMuted) =>
      afterMutation(nowMuted ? t('mute.mutedAnnouncement') : t('mute.unmutedAnnouncement')),
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
    reminder.error ??
    mute.error

  const data = detail.data
  const going = data?.roster.filter((r) => r.status === 'going' && !r.isWaitlisted) ?? []
  const maybe = data?.roster.filter((r) => r.status === 'maybe' && !r.isWaitlisted) ?? []
  const declined = data?.roster.filter((r) => r.status === 'declined') ?? []
  const pending = data?.roster.filter((r) => r.status === 'pending') ?? []
  const waitlisted = data?.roster.filter((r) => r.isWaitlisted) ?? []
  // A masked or guest roster entry's userId is absent on the wire (§5.1), so it
  // must never coincidentally match an unresolved session.userId (both undefined) —
  // both sides are required to be a real, matching id.
  const viewerEntry = data?.roster.find(
    (r) => Boolean(session?.userId) && r.userId === session?.userId,
  )
  // A-077 (d): a Removed participation keeps whatever inherited access the viewer
  // still has, but the shipped join mutation refuses a request from it
  // (DomainError.RemovalIsPermanent) — the request-to-join action is not offered.
  // Hidden by default until the roster has loaded — surfacing it early would flash
  // it on for a Removed viewer whose status isn't knowable yet.
  const canRequestJoin = Boolean(data) && viewerEntry?.status !== 'removed'
  const inProgress = data ? isSessionInProgress(data) : false
  const maxCapacity = data?.maxCapacity ?? null

  function rosterRow(entry: (typeof going)[number]) {
    return (
      <li
        key={entry.participationId}
        className="border-t border-ink/25 py-2 first:border-t-0"
        data-testid={`roster-entry-${entry.participationId}`}
      >
        <RosterIdentity
          actions={
            isOrganizer && entry.origin === 'guestLink' ? (
              <RemoveGuestDialog
                displayName={entry.displayName ?? t('detail.anonymous')}
                pending={removeGuestParticipant.isPending}
                onConfirm={() => removeGuestParticipant.mutate(entry.participationId)}
              />
            ) : undefined
          }
          avatar={
            <AvatarDisc
              emoji={entry.avatar?.emoji}
              imgUrl={avatarImgUrl(entry.avatar)}
              masked={entry.displayName === null}
              seed={entry.userId ?? entry.guestIdentityId ?? entry.participationId}
              size="xs"
            />
          }
          displayName={entry.displayName ?? t('detail.anonymous')}
          masked={entry.displayName === null}
        />
      </li>
    )
  }

  return (
    <main data-testid="screen-session-detail">
      <PageScaffold>
        <PageHeader
          actions={
            data && isOrganizer ? (
              <>
                <Link
                  className={`inline-flex items-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                  to={`/sessions/${sessionId}/edit`}
                >
                  {t('edit.heading')}
                </Link>
                <Link
                  className={`inline-flex items-center gap-2 px-4 py-2 text-ink bg-card ${raisedInteractiveClasses}`}
                  to={`/sessions/${sessionId}/invite`}
                >
                  {t('invite.heading')}
                </Link>
              </>
            ) : undefined
          }
          live
          meta={
            data ? (
              <div className="flex w-full flex-col gap-2">
                <div className="flex flex-wrap items-center gap-2">
                  <SessionStatusChip status={data.status} />
                  {inProgress && <span className={chipClasses}>{t('detail.inProgress')}</span>}
                </div>
                {data.status === 'unconfirmed' && (
                  <p className="font-body text-body text-ink">
                    {t('detail.progress', {
                      going: going.length,
                      min: Number(data.minAttendance),
                    })}
                    {maxCapacity !== null && ` ${t('detail.progressWaitlist')}`}
                  </p>
                )}
              </div>
            ) : undefined
          }
          title={data?.title ?? t('common.loading', { ns: 'common' })}
        />

        <p aria-live="polite" className="text-ink">
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
            <div className="flex flex-col gap-3">
              <div className="flex flex-wrap items-center gap-2">
                <span className={quietChipClasses}>
                  {new Intl.DateTimeFormat(i18n.language, {
                    dateStyle: 'medium',
                    timeStyle: 'short',
                    timeZone: viewerTimeZone,
                  }).format(new Date(data.startTime))}
                </span>
                {data.platform && <span className={quietChipClasses}>{data.platform}</span>}
                {data.game?.name && <span className={quietChipClasses}>{data.game.name}</span>}
                {data.muted && <span className={quietChipClasses}>{t('detail.muted')}</span>}
              </div>
              {data.notes && <p className="text-ink">{data.notes}</p>}
            </div>

            <SectionCard title={t('rsvp.heading')} titleId="rsvp-heading">
              <div aria-labelledby="rsvp-heading" role="group">
                <SegmentStrip>
                  {(['Going', 'Maybe', 'Declined'] as const).map((choice) => {
                    const pressed = viewerEntry?.status === choice.toLowerCase()
                    return (
                      <button
                        key={choice}
                        aria-pressed={pressed}
                        className={segmentItemClasses(pressed)}
                        disabled={respond.isPending}
                        type="button"
                        onClick={() => respond.mutate(choice)}
                      >
                        {t(`rsvp.${choice.toLowerCase()}`)}
                      </button>
                    )
                  })}
                </SegmentStrip>
              </div>

              <div className="flex flex-wrap gap-2">
                <Button
                  disabled={leave.isPending}
                  type="button"
                  variant="ghost"
                  onClick={() => leave.mutate()}
                >
                  {t('rsvp.leave')}
                </Button>
                {canRequestJoin && (
                  <>
                    <Button
                      disabled={join.isPending}
                      type="button"
                      variant="ghost"
                      onClick={() => join.mutate()}
                    >
                      {t('join.request')}
                    </Button>
                    <Button
                      disabled={withdraw.isPending}
                      type="button"
                      variant="ghost"
                      onClick={() => withdraw.mutate()}
                    >
                      {t('join.withdraw')}
                    </Button>
                  </>
                )}
              </div>
            </SectionCard>

            {isOrganizer && joinRequests.data && joinRequests.data.items.length > 0 && (
              <Section
                title={`${t('detail.joinRequests')} (${joinRequests.data.items.length})`}
                titleId="join-requests-heading"
              >
                <ul>
                  {joinRequests.data.items.map((request) => (
                    <li
                      key={request.id}
                      className="flex flex-wrap items-center justify-between gap-2 border-t border-ink/25 py-2 first:border-t-0"
                    >
                      <span className="text-ink">
                        {request.requester?.displayName ?? t('detail.anonymous')}
                      </span>
                      <span className="flex gap-2">
                        <Button
                          type="button"
                          variant="ghost"
                          onClick={() => approve.mutate(request.id)}
                        >
                          {t('join.approve')}
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          onClick={() => reject.mutate(request.id)}
                        >
                          {t('join.reject')}
                        </Button>
                      </span>
                    </li>
                  ))}
                </ul>
              </Section>
            )}

            <Section
              label={t('a11y.rosterLabel')}
              labelledBy="roster-heading"
              title={t('detail.roster')}
              titleId="roster-heading"
            >
              {(
                [
                  ['going', going],
                  ['maybe', maybe],
                  ['declined', declined],
                  ['pending', pending],
                ] as const
              ).map(([key, entries]) => (
                <div key={key} className="flex flex-col gap-2">
                  <h3 className="font-display font-semibold text-ink">
                    {t(`detail.${key}`)} ({entries.length})
                  </h3>
                  <ul>{entries.map(rosterRow)}</ul>
                </div>
              ))}
            </Section>

            {waitlisted.length > 0 && (
              <Section
                label={t('a11y.waitlistLabel')}
                labelledBy="waitlist-heading"
                title={`${t('waitlist.heading')} (${waitlisted.length})`}
                titleId="waitlist-heading"
              >
                <ul>
                  {waitlisted.map((entry) => (
                    <li
                      key={entry.participationId}
                      className="border-t border-ink/25 py-2 first:border-t-0"
                    >
                      <RosterIdentity
                        actions={
                          isOrganizer ? (
                            <>
                              <Button
                                type="button"
                                variant="ghost"
                                onClick={() => promote.mutate(entry.participationId)}
                              >
                                {t('waitlist.promote')}
                              </Button>
                              <Button
                                type="button"
                                variant="ghost"
                                onClick={() => rejectWaitlist.mutate(entry.participationId)}
                              >
                                {t('waitlist.reject')}
                              </Button>
                            </>
                          ) : undefined
                        }
                        avatar={
                          <AvatarDisc
                            emoji={entry.avatar?.emoji}
                            imgUrl={avatarImgUrl(entry.avatar)}
                            masked={entry.displayName === null}
                            seed={
                              entry.userId ?? entry.guestIdentityId ?? entry.participationId
                            }
                            size="xs"
                          />
                        }
                        displayName={entry.displayName ?? t('detail.anonymous')}
                        masked={entry.displayName === null}
                      />
                    </li>
                  ))}
                </ul>
              </Section>
            )}

            <Section
              title={t('detail.notificationsHeading')}
              titleId="session-notifications-heading"
            >
              <div className="flex flex-wrap items-end gap-6">
                <div className="flex flex-col gap-1.5">
                  <label
                    className="font-body font-semibold text-ink"
                    htmlFor="reminder-select"
                  >
                    {t('reminder.label')}
                  </label>
                  <select
                    className="control-frame control-select min-h-11 px-3 py-2 font-body text-body"
                    id="reminder-select"
                    value={viewerEntry?.reminderChoice ?? ''}
                    onChange={(event) =>
                      reminder.mutate((event.target.value || null) as ReminderChoiceValue)
                    }
                  >
                    {reminderOptions.map((option) => (
                      <option key={option.key} value={option.value ?? ''}>
                        {t(option.key)}
                      </option>
                    ))}
                  </select>
                </div>
                <Button
                  aria-busy={mute.isPending}
                  aria-pressed={data.muted}
                  disabled={mute.isPending}
                  type="button"
                  variant={data.muted ? 'primary' : 'neutral'}
                  onClick={() => mute.mutate()}
                >
                  {data.muted ? t('mute.off') : t('mute.on')}
                </Button>
              </div>
              <p className="font-body text-meta text-ink-soft">{t('mute.copy')}</p>
            </Section>

            {isOrganizer && (
              <SectionCard
                title={t('detail.dangerZone')}
                titleId="session-danger-heading"
              >
                <div className="flex flex-wrap gap-2">
                  <CancelSessionDialog
                    pending={cancel.isPending}
                    onConfirm={() => cancel.mutate()}
                  />
                </div>
              </SectionCard>
            )}
          </>
        )}
      </PageScaffold>
    </main>
  )
}

