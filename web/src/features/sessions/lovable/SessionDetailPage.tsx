import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { CalendarClock, Users } from 'lucide-react'
import { Link, Navigate, useParams } from 'react-router'
import { isApiError } from '../../../api/client'
import { useSession } from '../../../auth/useSession'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { avatarImgUrl } from '../../friends/avatarImage'
import { Button } from '../../../components/ui/button'
import {
  chipClasses,
  neutralCtaClasses,
  raisedInteractiveClasses,
  segmentItemClasses,
} from '../../../components/ui/classes'
import { Dialog } from '../../../components/ui/dialog'
import { PageHeader } from '../../../components/shell/PageHeader'
import { IdentityRow } from '../../../components/ui/identityRow'
import { SegmentStrip } from '../../../components/ui/segmentStrip'
import { StatusBanner } from '../../../components/ui/statusBanner'
import { instantToFakeLocal } from '../../availability/lovable/zone'
import { ShareTag } from '../../groups/ShareTag'
import { StatusPill } from './SessionBits'
import { isKnownSessionStatus, sessionPlatformLabel } from './sessionData'
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
} from '../sessionQueries'

/**
 * RSVP is a pressed-button `SegmentStrip`, not a radiogroup — D-084's
 * arrow-key hazard is why RSVP uses a pressed-button group
 * (`components/ui/segmentStrip.tsx` §doc).
 *
 * Guest-link minting has no home on this page (that surface is
 * `SessionInvitePage.tsx`); only the roster's per-row "remove guest" action
 * for a `guestLink`-origin participant lives here.
 *
 * Mandated below-`lg` stacking order: summary(1) -> main RSVP column(2) ->
 * roster(3) -> waitlist(3, same tier) -> join requests(4) ->
 * notifications(6) -> danger zone(7, last).
 */

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
        <Button disabled={pending} type="button" variant="danger">
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
            className="mt-2 text-ink-soft"
            id="cancel-session-description"
          >
            {t('cancel.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
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

function RemoveGuestDialog({ displayName, pending, onConfirm }: RemoveGuestDialogProps) {
  const { t } = useTranslation('sessions')
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button
          aria-label={`${t('invite.removeGuest')} ${displayName}`}
          disabled={pending}
          type="button"
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
            className="mt-2 text-ink-soft"
            id="remove-guest-description"
          >
            {t('invite.removeGuestConfirm')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
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
 * computed `variant` prop so the D-076 row keeps `IdentityRow`'s own
 * type-level guarantee that no username can ride along with it — carried
 * unchanged from the flat page's own `RosterIdentity`.
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
  const [announcement, setAnnouncement] = useState('')
  const { data: session } = useSession()

  const detail = useQuery({
    queryKey: sessionKeys.detail(sessionId),
    queryFn: () => getSessionDetail(sessionId),
    enabled: Boolean(sessionId),
    retry: false,
  })

  // D-038/D-046: times render in the viewer's profile time zone, not the
  // browser's, falling back to the browser zone only until the profile
  // query resolves. `instantToFakeLocal` + `timeZone: 'UTC'` formatting is
  // the same zoned-conversion boundary `./sessionData`'s list mapper uses.
  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const viewerTimeZone =
    profile.data?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone

  const isOrganizer =
    Boolean(detail.data) && detail.data?.organizerId === session?.userId

  // Mirrors `useSessionScope` in `SessionListPage.tsx`: one query per cursor
  // page, appended to as the organizer asks for more, rather than a single
  // fixed-size fetch that silently hides everything past page one.
  const [joinRequestCursors, setJoinRequestCursors] = useState<
    (string | undefined)[]
  >([undefined])
  const joinRequestPages = useQueries({
    queries: joinRequestCursors.map((cursor) => ({
      queryKey: sessionKeys.joinRequests(sessionId, cursor),
      queryFn: () => getJoinRequests(sessionId, cursor),
      enabled: Boolean(sessionId) && isOrganizer,
    })),
  })
  const joinRequestItems = joinRequestPages.flatMap(
    (page) => page.data?.items ?? [],
  )
  const joinRequestsLastPage = joinRequestPages[joinRequestPages.length - 1]
  const joinRequestsNextCursor =
    joinRequestsLastPage?.data?.nextCursor ?? undefined
  const joinRequestsFetchingMore =
    joinRequestPages.length > 1 && (joinRequestsLastPage?.isFetching ?? false)
  const joinRequests = {
    isError: joinRequestPages.some((page) => page.error),
    isPending: joinRequestPages[0]?.isPending ?? true,
    data:
      joinRequestPages[0]?.data === undefined
        ? undefined
        : { items: joinRequestItems, nextCursor: joinRequestsNextCursor },
    error: joinRequestPages.find((page) => page.error)?.error,
    refetch: () => {
      setJoinRequestCursors([undefined])
      return joinRequestPages[0]?.refetch()
    },
  }

  async function afterMutation(message: string, options?: { removeDetail?: boolean }) {
    setAnnouncement(message)
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
    mute.error ??
    joinRequests.error

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

  const startText = data
    ? new Intl.DateTimeFormat(i18n.language, {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone: 'UTC',
      }).format(instantToFakeLocal(data.startTime, viewerTimeZone))
    : ''

  const endText = data
    ? new Intl.DateTimeFormat(i18n.language, {
        timeStyle: 'short',
        timeZone: 'UTC',
      }).format(
        new Date(
          instantToFakeLocal(data.startTime, viewerTimeZone).getTime() +
            Number(data.durationMinutes) * 60_000,
        ),
      )
    : ''

  function rosterRow(entry: (typeof going)[number]) {
    return (
      <li
        key={entry.participationId}
        className="flex items-center justify-between gap-2 border-t border-border py-2 first:border-t-0"
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

  const anyErrorMessage = anyError
    ? t(isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic', {
        defaultValue: t('errors.generic'),
      })
    : null

  return (
    <div className="min-h-screen bg-background pb-24" data-testid="screen-session-detail">
      <p aria-live="polite" className="sr-only" role="status">
        {announcement}
      </p>

      {detail.isPending && (
        <p className="p-6 text-ink-soft" role="status">
          {t('common.loading', { ns: 'common' })}
        </p>
      )}

      {anyErrorMessage && (
        <div className="mx-auto max-w-[1400px] px-3 pt-4 sm:px-6">
          <StatusBanner role="alert" variant="danger">
            <span ref={(node) => node?.focus()} tabIndex={-1}>
              {anyErrorMessage}
            </span>
          </StatusBanner>
        </div>
      )}

      {data && (
        <>
          <div className="mx-auto max-w-[1400px] px-3 sm:px-6">
            <PageHeader
              actions={
                isKnownSessionStatus(data.status) && (
                  <StatusPill label={t(`status.${data.status}`)} status={data.status} />
                )
              }
              back={{ label: t('nav.backToList'), to: '/sessions/upcoming' }}
              description={`${data.game ? `${data.game.name} · ` : ''}${sessionPlatformLabel(data.platform, t)}`}
              title={data.title}
            />
          </div>
          <main className="mx-auto grid max-w-[1400px] gap-4 px-3 pb-4 sm:px-6 lg:grid-cols-[minmax(0,1fr)_340px]">
          <div className="contents lg:order-2 lg:block lg:min-w-0 lg:space-y-4 lg:sticky lg:top-4 lg:self-start">
            <section className="order-1 surface p-4" data-testid="session-summary-card">
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-2 rounded-xl border border-border bg-muted/50 px-2.5 py-2">
                  <CalendarClock className="size-4 shrink-0" />
                  <span className="text-[12px] font-semibold tabular-nums">{startText}</span>
                </span>
                <span className="text-[12px] font-semibold text-ink-soft tabular-nums">
                  {t('detail.endsAt', { time: endText })}
                </span>
                {inProgress && <span className={chipClasses}>{t('detail.inProgress')}</span>}
                <ShareTag share={data} />
                {data.muted && (
                  <span className="rounded-full border border-border bg-muted px-2.5 py-1 text-[11px] font-semibold text-muted-foreground">
                    {t('detail.muted')}
                  </span>
                )}
              </div>

              {data.status === 'unconfirmed' && (
                <p className="mt-2 text-[12px] text-ink-soft">
                  {t('detail.progress', {
                    going: going.length,
                    min: Number(data.minAttendance),
                  })}
                  {maxCapacity !== null && ` ${t('detail.progressWaitlist')}`}
                </p>
              )}

              {data.notes && (
                <p className="mt-3 rounded-xl border border-border bg-muted/50 p-2.5 text-[12px] text-ink">
                  {data.notes}
                </p>
              )}

              {isOrganizer && (
                <div className="mt-3 flex flex-wrap gap-2">
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
                </div>
              )}
            </section>

            <section className="order-3 surface p-4">
              <h2
                className="flex items-center gap-2 font-display text-base font-extrabold"
                id="roster-heading"
              >
                <Users className="size-4" /> {t('detail.roster')}
              </h2>
              <section aria-label={t('a11y.rosterLabel')} aria-labelledby="roster-heading">
                {(
                  [
                    ['going', going],
                    ['maybe', maybe],
                    ['declined', declined],
                    ['pending', pending],
                  ] as const
                ).map(([key, entries]) => (
                  <div key={key} className="mt-3 first:mt-2">
                    <h3 className="font-display text-[13px] font-bold text-muted-foreground">
                      {t(`detail.${key}`)} ({entries.length})
                    </h3>
                    <ul className="mt-1.5 space-y-1.5">{entries.map(rosterRow)}</ul>
                  </div>
                ))}
              </section>
            </section>

            {waitlisted.length > 0 && (
              <section className="order-3 surface p-4">
                <h2
                  className="font-display text-base font-extrabold"
                  id="waitlist-heading"
                >
                  {t('waitlist.heading')} ({waitlisted.length})
                </h2>
                <ul
                  aria-label={t('a11y.waitlistLabel')}
                  aria-labelledby="waitlist-heading"
                  className="mt-2 space-y-1.5"
                >
                  {waitlisted.map((entry) => (
                    <li
                      key={entry.participationId}
                      className="border-t border-border py-2 first:border-t-0"
                    >
                      <RosterIdentity
                        actions={
                          isOrganizer ? (
                            <>
                              <Button
                                aria-busy={
                                  promote.isPending &&
                                  promote.variables === entry.participationId
                                }
                                disabled={
                                  promote.isPending &&
                                  promote.variables === entry.participationId
                                }
                                type="button"
                                variant="ghost"
                                onClick={() => promote.mutate(entry.participationId)}
                              >
                                {t('waitlist.promote')}
                              </Button>
                              <Button
                                aria-busy={
                                  rejectWaitlist.isPending &&
                                  rejectWaitlist.variables === entry.participationId
                                }
                                disabled={
                                  rejectWaitlist.isPending &&
                                  rejectWaitlist.variables === entry.participationId
                                }
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
              </section>
            )}

            {isOrganizer && joinRequests.isError && (
              <section className="order-4 surface p-4" data-testid="join-requests-error">
                <h2
                  className="font-display text-base font-extrabold"
                  id="join-requests-heading"
                >
                  {t('detail.joinRequests')}
                </h2>
                <StatusBanner
                  actions={
                    <button
                      className={neutralCtaClasses}
                      type="button"
                      onClick={() => joinRequests.refetch()}
                    >
                      {t('actions.retry', { ns: 'common' })}
                    </button>
                  }
                  role="alert"
                  variant="danger"
                >
                  {t('errors.generic', { ns: 'errors' })}
                </StatusBanner>
              </section>
            )}

            {isOrganizer &&
              !joinRequests.isError &&
              joinRequests.data &&
              joinRequests.data.items.length > 0 && (
                <section className="order-4 surface p-4">
                  <h2
                    className="font-display text-base font-extrabold"
                    id="join-requests-heading"
                  >
                    {t('detail.joinRequests')} ({joinRequests.data.items.length})
                  </h2>
                  <ul className="mt-2 space-y-1.5">
                    {joinRequests.data.items.map((request) => (
                      <li
                        key={request.id}
                        className="flex flex-wrap items-center justify-between gap-2 border-t border-border py-2 first:border-t-0"
                      >
                        <span className="text-[13px] font-semibold">
                          {request.requester?.displayName ?? t('detail.anonymous')}
                        </span>
                        <span className="flex gap-2">
                          <Button
                            aria-busy={approve.isPending && approve.variables === request.id}
                            disabled={approve.isPending && approve.variables === request.id}
                            type="button"
                            variant="ghost"
                            onClick={() => approve.mutate(request.id)}
                          >
                            {t('join.approve')}
                          </Button>
                          <Button
                            aria-busy={reject.isPending && reject.variables === request.id}
                            disabled={reject.isPending && reject.variables === request.id}
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
                  {joinRequestsNextCursor && (
                    <Button
                      aria-busy={joinRequestsFetchingMore}
                      className="mt-2"
                      disabled={joinRequestsFetchingMore}
                      variant="neutral"
                      onClick={() =>
                        setJoinRequestCursors((current) => [
                          ...current,
                          joinRequestsNextCursor,
                        ])
                      }
                    >
                      {t('list.loadMore')}
                    </Button>
                  )}
                </section>
              )}

            <section className="order-6 surface p-4">
              <h2 className="font-display text-base font-extrabold">
                {t('detail.notificationsHeading')}
              </h2>
              <div className="mt-3 flex flex-wrap items-end gap-4">
                <div className="flex flex-col gap-1.5">
                  <label
                    className="text-[12px] font-bold text-muted-foreground"
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
              <p className="mt-2 text-[12px] text-muted-foreground">{t('mute.copy')}</p>
            </section>

            {isOrganizer && (
              <section className="order-7 surface border-danger/40 p-4">
                <h2 className="font-display text-base font-extrabold">
                  {t('detail.dangerZone')}
                </h2>
                <div className="mt-3 flex flex-wrap gap-2">
                  <CancelSessionDialog
                    pending={cancel.isPending}
                    onConfirm={() => cancel.mutate()}
                  />
                </div>
              </section>
            )}
          </div>

          <section className="order-2 min-w-0 space-y-3 lg:order-1">
            <div
              aria-labelledby="rsvp-heading"
              className="surface p-4"
              role="group"
            >
              <h2 className="font-display text-lg font-extrabold" id="rsvp-heading">
                {t('rsvp.heading')}
              </h2>
              <div className="mt-3">
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

              <div className="mt-3 flex flex-wrap gap-2">
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
            </div>
          </section>
          </main>
        </>
      )}
    </div>
  )
}
