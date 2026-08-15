import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { fieldSelectClasses } from '../../components/ui/classes'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import {
  disablePushInThisBrowser,
  enablePushInThisBrowser,
  isPushEnabledInThisBrowser,
  isPushSupported,
} from '../notifications/pushSubscription'
import { SettingsNavigation } from './BlockedUsersPage'

type ReminderDefaultValue =
  | 'Off'
  | 'Minutes15'
  | 'Minutes30'
  | 'Hours1'
  | 'Hours3'
  | 'Hours12'
  | 'Hours24'

// D-082's seven choices — the account default is always one of these (never null; "use account
// default" is the SESSION-level option, not this one).
const reminderOptions: { value: ReminderDefaultValue; key: string }[] = [
  { value: 'Off', key: 'notificationsPage.reminderDefault.off' },
  { value: 'Minutes15', key: 'notificationsPage.reminderDefault.minutes15' },
  { value: 'Minutes30', key: 'notificationsPage.reminderDefault.minutes30' },
  { value: 'Hours1', key: 'notificationsPage.reminderDefault.hours1' },
  { value: 'Hours3', key: 'notificationsPage.reminderDefault.hours3' },
  { value: 'Hours12', key: 'notificationsPage.reminderDefault.hours12' },
  { value: 'Hours24', key: 'notificationsPage.reminderDefault.hours24' },
]

// The `/me/reminder-default` route speaks the `ReminderOffset` enum's member names on the wire
// (`Min15`/`Min30`, not `Minutes15`/`Minutes30`) — unlike the per-session reminder route, which
// SessionContracts translates to `Minutes15`/`Minutes30`. This page's option values stay the
// friendlier `Minutes15`/`Minutes30` for the i18n keys above; these maps convert at the boundary.
const toWireValue: Record<ReminderDefaultValue, string> = {
  Off: 'Off',
  Minutes15: 'Min15',
  Minutes30: 'Min30',
  Hours1: 'Hours1',
  Hours3: 'Hours3',
  Hours12: 'Hours12',
  Hours24: 'Hours24',
}

const fromWireValue: Record<string, ReminderDefaultValue> = {
  Off: 'Off',
  Min15: 'Minutes15',
  Min30: 'Minutes30',
  Hours1: 'Hours1',
  Hours3: 'Hours3',
  Hours12: 'Hours12',
  Hours24: 'Hours24',
}

const csrf = { 'X-CSRF-TOKEN': '' }

async function changeReminderDefault(reminderDefault: ReminderDefaultValue) {
  await client.PUT('/api/v1/me/reminder-default', {
    params: { header: csrf },
    body: { reminderDefault: toWireValue[reminderDefault] },
  })
}

async function changeEmailNotifications(enabled: boolean) {
  await client.PUT('/api/v1/me/email-notifications', {
    params: { header: csrf },
    body: { enabled },
  })
}

/**
 * `/settings/notifications` (design §5, ruling 3): per-browser push on/off, the account default
 * reminder (D-082), a link out to per-session/poll muting (which lives on each session/poll's own
 * page, not here — a per-type opt-out matrix is out of scope), and the account email-notifications
 * toggle (D-189, email-only — in-app notifications are unaffected).
 */
export function NotificationSettingsPage() {
  const { t } = useTranslation(['settings', 'errors'])
  const queryClient = useQueryClient()
  const [pushEnabled, setPushEnabled] = useState(() => isPushEnabledInThisBrowser())
  const [pushError, setPushError] = useState<unknown>(null)
  const [reminderSaved, setReminderSaved] = useState(false)
  const supported = isPushSupported()

  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const emailNotificationsEnabled = profile.data?.emailNotificationsEnabled ?? true

  const togglePush = useMutation({
    mutationFn: async () => {
      if (pushEnabled) {
        await disablePushInThisBrowser()
        return false
      }
      await enablePushInThisBrowser()
      return true
    },
    onSuccess: (nowEnabled) => {
      setPushError(null)
      setPushEnabled(nowEnabled)
    },
    onError: (error) => {
      setPushError(error)
    },
  })

  const reminderDefault = useMutation({
    mutationFn: changeReminderDefault,
    onSuccess: async () => {
      setReminderSaved(true)
      await queryClient.invalidateQueries({ queryKey: viewerProfileKey })
    },
  })

  const emailNotifications = useMutation({
    mutationFn: changeEmailNotifications,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: viewerProfileKey })
    },
  })

  return (
    <main data-testid="screen-settings-notifications">
      <PageScaffold>
        <PageHeader
          eyebrow={t('navigation.label')}
          title={t('notificationsPage.heading')}
        />
        <SettingsNavigation />

        <Section
          description={t('notificationsPage.push.description')}
          title={t('notificationsPage.push.title')}
          titleId="push-heading"
        >
          {!supported ? (
            <p className="text-ink-soft" role="status">
              {t('notificationsPage.push.unsupported')}
            </p>
          ) : (
            <Button
              aria-busy={togglePush.isPending}
              aria-pressed={pushEnabled}
              className="self-start"
              disabled={togglePush.isPending}
              variant={pushEnabled ? 'primary' : 'neutral'}
              onClick={() => togglePush.mutate()}
            >
              {pushEnabled
                ? t('notificationsPage.push.toggleOff')
                : t('notificationsPage.push.toggleOn')}
            </Button>
          )}
          {Boolean(pushError) && (
            <StatusBanner role="alert" variant="danger">
              {t('notificationsPage.push.error')}
            </StatusBanner>
          )}
        </Section>

        <Section
          title={t('notificationsPage.reminderDefault.title')}
          titleId="reminder-default-heading"
        >
          <div className="flex max-w-form flex-col gap-2">
            <label
              className="block font-body font-semibold text-ink"
              htmlFor="reminder-default-select"
            >
              {t('notificationsPage.reminderDefault.label')}
            </label>
            <select
              className={fieldSelectClasses}
              disabled={profile.isPending}
              id="reminder-default-select"
              value={
                (profile.data?.reminderDefault &&
                  fromWireValue[profile.data.reminderDefault]) ??
                'Off'
              }
              onChange={(event) => {
                setReminderSaved(false)
                reminderDefault.mutate(event.target.value as ReminderDefaultValue)
              }}
            >
              {reminderOptions.map((option) => (
                <option key={option.key} value={option.value}>
                  {t(option.key)}
                </option>
              ))}
            </select>
            {reminderSaved && (
              <p className="text-ink" role="status">
                {t('notificationsPage.reminderDefault.saved')}
              </p>
            )}
            {reminderDefault.isError && (
              <StatusBanner role="alert" variant="danger">
                {t(
                  isApiError(reminderDefault.error)
                    ? reminderDefault.error.code
                    : 'generic',
                  { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) },
                )}
              </StatusBanner>
            )}
          </div>
        </Section>

        <Section
          description={t('notificationsPage.mute.description')}
          title={t('notificationsPage.mute.title')}
          titleId="mute-heading"
        >
          <Link
            className="self-start font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            to="/sessions/upcoming"
          >
            {t('notificationsPage.mute.link')}
          </Link>
        </Section>

        <Section
          description={t('notificationsPage.email.description')}
          title={t('notificationsPage.email.title')}
          titleId="email-notifications-heading"
        >
          <Button
            aria-busy={emailNotifications.isPending}
            aria-pressed={emailNotificationsEnabled}
            className="self-start"
            disabled={emailNotifications.isPending || profile.isPending}
            variant={emailNotificationsEnabled ? 'primary' : 'neutral'}
            onClick={() => emailNotifications.mutate(!emailNotificationsEnabled)}
          >
            {emailNotificationsEnabled
              ? t('notificationsPage.email.toggleOff')
              : t('notificationsPage.email.toggleOn')}
          </Button>
          {emailNotifications.isError && (
            <StatusBanner role="alert" variant="danger">
              {t(
                isApiError(emailNotifications.error)
                  ? emailNotifications.error.code
                  : 'generic',
                { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) },
              )}
            </StatusBanner>
          )}
        </Section>
      </PageScaffold>
    </main>
  )
}
