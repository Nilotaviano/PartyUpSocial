import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Checkbox } from '../../components/ui/checkbox'
import { Dialog } from '../../components/ui/dialog'
import { Label } from '../../components/ui/label'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import {
  adminKeys,
  banAdminUser,
  banAndRemoveContent,
  getAdminUser,
  invalidateAdminUsers,
  numeric,
  unbanAdminUser,
} from './adminQueries'

const dialogContentClasses =
  'fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none'

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-ink-soft">{label}</dt>
      <dd className="font-display text-ink">{value}</dd>
    </div>
  )
}

/**
 * D-180's plain arm: ban and unban are ordinary actions behind a single
 * confirmation. Neither ever carries a content-removal option — the removal
 * dialog below is a wholly separate action, never a checkbox riding this one.
 */
function PlainActionDialog({
  action,
  title,
  body,
  confirm,
  cancel,
  pending,
  onConfirm,
  testId,
}: {
  action: string
  title: string
  body: string
  confirm: string
  cancel: string
  pending: boolean
  onConfirm: () => void
  testId: string
}) {
  const [open, setOpen] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const descriptionId = `${testId}-description`

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <Button data-testid={testId} disabled={pending} variant="neutral">
          {action}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby={descriptionId}
          className={dialogContentClasses}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {title}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id={descriptionId}>
            {body}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
                type="button"
              >
                {cancel}
              </button>
            </Dialog.Close>
            <Button
              type="button"
              variant="neutral"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {confirm}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/**
 * D-180's destructive arm (IMPLEMENTATION_PLAN.md:149): content removal is its
 * own action with its own confirmation flow — a danger-styled trigger, an
 * explicit irreversibility warning and a deliberate acknowledgement that must
 * be ticked before the confirm button is enabled. It is never reachable from
 * the plain ban dialog above.
 */
function RemoveContentDialog({
  pending,
  onConfirm,
}: {
  pending: boolean
  onConfirm: () => void
}) {
  const { t } = useTranslation('admin')
  const [open, setOpen] = useState(false)
  const [acknowledged, setAcknowledged] = useState(false)
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        setAcknowledged(false)
      }}
    >
      <Dialog.Trigger asChild>
        <Button data-testid="admin-remove-content-action" disabled={pending} variant="danger">
          {t('user.removeContent.action')}
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="admin-remove-content-description"
          className={dialogContentClasses}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('user.removeContent.title')}
          </Dialog.Title>
          <Dialog.Description
            className="mt-2 text-ink-soft"
            id="admin-remove-content-description"
          >
            {t('user.removeContent.body')}
          </Dialog.Description>
          <p className="mt-3 font-display font-semibold text-ink">
            {t('user.removeContent.warning')}
          </p>
          <div className="mt-4 flex items-center gap-2">
            <Checkbox.Root
              checked={acknowledged}
              id="admin-remove-content-acknowledge"
              onCheckedChange={(checked) => setAcknowledged(checked === true)}
            />
            <Label htmlFor="admin-remove-content-acknowledge">
              {t('user.removeContent.acknowledge')}
            </Label>
          </div>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={`inline-flex items-center justify-center gap-2 bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
                type="button"
              >
                {t('user.removeContent.cancel')}
              </button>
            </Dialog.Close>
            <Button
              disabled={!acknowledged}
              type="button"
              variant="danger"
              onClick={() => {
                setOpen(false)
                onConfirm()
              }}
            >
              {t('user.removeContent.confirm')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export function AdminUserDetailPage() {
  const { t, i18n } = useTranslation(['admin', 'common'])
  const { userId = '' } = useParams()
  const queryClient = useQueryClient()
  const [announcement, setAnnouncement] = useState('')
  const user = useQuery({
    queryKey: adminKeys.user(userId),
    queryFn: () => getAdminUser(userId),
    retry: false,
  })

  const afterMutation = async (message: string) => {
    setAnnouncement(message)
    await invalidateAdminUsers(queryClient)
  }

  const ban = useMutation({
    mutationFn: () => banAdminUser(userId),
    onSuccess: () => afterMutation(t('user.ban.done')),
  })
  const unban = useMutation({
    mutationFn: () => unbanAdminUser(userId),
    onSuccess: () => afterMutation(t('user.unban.done')),
  })
  const removeContent = useMutation({
    mutationFn: () => banAndRemoveContent(userId),
    onSuccess: () => afterMutation(t('user.removeContent.done')),
  })

  // A new action clears what the previous one left on screen, so a stale
  // role="alert" failure never renders beside the role="status" success of the
  // action the admin just took.
  const start = (mutation: { mutate: () => void }) => {
    setAnnouncement('')
    ban.reset()
    unban.reset()
    removeContent.reset()
    mutation.mutate()
  }

  // D-177 fail-closed parity — see AdminDashboardPage's own branch.
  if (user.isError && isApiError(user.error) && user.error.status === 404) {
    return (
      <main data-testid="screen-admin-user">
        <PageScaffold width="form">
          <p className="text-ink" role="alert">
            {t('errors.codes.not_found')}
          </p>
        </PageScaffold>
      </main>
    )
  }

  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' })
  const dateTime = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
  const number = new Intl.NumberFormat(i18n.language)
  const data = user.data
  const name = data?.displayName ?? data?.username ?? t('user.unknown')
  const mutationError = ban.error ?? unban.error ?? removeContent.error
  const anyError = user.error ?? mutationError
  const pending = ban.isPending || unban.isPending || removeContent.isPending

  return (
    <main data-testid="screen-admin-user">
      <PageScaffold>
        <PageHeader
          actions={
            <Link
              className={`inline-flex items-center justify-center bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
              to="/admin/users"
            >
              {t('user.back')}
            </Link>
          }
          title={name}
        />

        {user.isPending && (
          <p className="motion-safe:animate-pulse text-ink-soft" role="status">
            {t('user.loading')}
          </p>
        )}

        <p role="status">{announcement}</p>

        {anyError && (
          <StatusBanner role="alert" variant="danger">
            {t(
              isApiError(anyError) ? `errors.codes.${anyError.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        {data && (
          <>
            <Section title={t('user.statsHeading')} titleId="admin-user-stats">
              <dl className="grid gap-3 sm:grid-cols-2">
                <Field
                  label={t('user.fields.username')}
                  value={data.username ?? t('user.unknown')}
                />
                <Field
                  label={t('user.fields.displayName')}
                  value={data.displayName ?? t('user.unknown')}
                />
                <Field
                  label={t('user.fields.email')}
                  value={data.email ?? t('user.unknown')}
                />
                <Field
                  label={t('user.fields.registered')}
                  value={date.format(new Date(data.created))}
                />
                <Field
                  label={t('user.fields.lastActivity')}
                  value={
                    data.lastSeenAt === null
                      ? t('user.neverActive')
                      : dateTime.format(new Date(data.lastSeenAt))
                  }
                />
                <Field
                  label={t('user.fields.sessionsOrganized')}
                  value={number.format(numeric(data.sessionsOrganized))}
                />
                <Field
                  label={t('user.fields.sessionsJoined')}
                  value={number.format(numeric(data.sessionsJoined))}
                />
                <Field
                  label={t('user.fields.friendCount')}
                  value={number.format(numeric(data.friendCount))}
                />
                <Field
                  label={t('user.fields.lifecycle')}
                  value={t(`user.lifecycles.${data.lifecycle}`, {
                    defaultValue: t('user.lifecycles.unknown'),
                  })}
                />
                <Field
                  label={t('user.fields.banState')}
                  value={
                    data.bannedAt === null
                      ? t('user.notBanned')
                      : t('user.banned', { date: date.format(new Date(data.bannedAt)) })
                  }
                />
              </dl>
            </Section>

            <div className="flex flex-wrap gap-2">
              {data.bannedAt === null ? (
                <PlainActionDialog
                  action={t('user.ban.action')}
                  body={t('user.ban.body')}
                  cancel={t('user.ban.cancel')}
                  confirm={t('user.ban.confirm')}
                  pending={pending}
                  testId="admin-ban-action"
                  title={t('user.ban.title')}
                  onConfirm={() => start(ban)}
                />
              ) : (
                <PlainActionDialog
                  action={t('user.unban.action')}
                  body={t('user.unban.body')}
                  cancel={t('user.unban.cancel')}
                  confirm={t('user.unban.confirm')}
                  pending={pending}
                  testId="admin-unban-action"
                  title={t('user.unban.title')}
                  onConfirm={() => start(unban)}
                />
              )}
            </div>

            {/*
              D-179/D-180: content removal never rides the ban action as an
              option — it is its own row inside the hazard boundary, so the
              irreversible arm is the only thing this card contains.
            */}
            <SectionCard title={t('user.dangerZone')}>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <p className="min-w-0 flex-1 text-ink-soft">
                  {t('user.removeContent.warning')}
                </p>
                <RemoveContentDialog
                  pending={pending}
                  onConfirm={() => start(removeContent)}
                />
              </div>
            </SectionCard>
          </>
        )}
      </PageScaffold>
    </main>
  )
}
