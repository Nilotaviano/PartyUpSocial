import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import {
  adminKeys,
  getAdminDashboard,
  numeric,
  optionalNumeric,
  type AdminUsageWindow,
} from './adminQueries'

function Metric({
  label,
  value,
  valueLabel,
}: {
  label: string
  value: string
  valueLabel?: string
}) {
  return (
    <div className="rounded-control border-2 border-ink/25 bg-card p-4">
      <dt className="text-ink-soft">{label}</dt>
      <dd
        aria-label={valueLabel}
        className="text-h2 font-display font-bold text-ink"
      >
        {value}
      </dd>
    </div>
  )
}

function useAdminFormatters() {
  const { i18n } = useTranslation('admin')
  // D-060/D-146: every display formatter is pinned to `i18n.language` (the
  // account's own locale), never the browser default.
  return {
    number: new Intl.NumberFormat(i18n.language),
    percent: new Intl.NumberFormat(i18n.language, {
      style: 'percent',
      maximumFractionDigits: 1,
    }),
    date: new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }),
    dateTime: new Intl.DateTimeFormat(i18n.language, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }),
  }
}

function UsageWindowPanel({
  headingId,
  title,
  window,
}: {
  headingId: string
  title: string
  window: AdminUsageWindow
}) {
  const { t } = useTranslation('admin')
  const { number, percent, date } = useAdminFormatters()
  const rate = optionalNumeric(window.confirmationRate)

  return (
    <Section
      description={t('dashboard.windows.since', {
        date: date.format(new Date(window.since)),
      })}
      title={title}
      titleId={headingId}
    >
      <dl className="grid gap-3 sm:grid-cols-2">
        <Metric
          label={t('dashboard.metrics.registrations')}
          value={number.format(numeric(window.registrations))}
        />
        <Metric
          label={t('dashboard.metrics.activeUsers')}
          value={number.format(numeric(window.activeUsers))}
        />
        <Metric
          label={t('dashboard.metrics.sessionsCreated')}
          value={number.format(numeric(window.sessionsCreated))}
        />
        <Metric
          label={t('dashboard.metrics.pollsCreated')}
          value={number.format(numeric(window.pollsCreated))}
        />
        {/*
          S1 verification caveat (PHASE8_DESIGN.md §3 S3): `sessionsCreated`
          already contains poll-derived sessions, so the two counts above are
          never summed into a "proposals" figure. The proposals figure IS the
          confirmation denominator, which counts a finalized poll exactly once.
        */}
        <Metric
          label={t('dashboard.metrics.proposals')}
          value={number.format(numeric(window.confirmationDenominator))}
        />
        <Metric
          label={t('dashboard.metrics.confirmed')}
          value={number.format(numeric(window.confirmationSuccesses))}
        />
        <Metric
          label={t('dashboard.metrics.confirmationRate')}
          value={rate === null ? t('dashboard.noValue') : percent.format(rate)}
          valueLabel={rate === null ? t('dashboard.noValueLabel') : undefined}
        />
      </dl>
      <div className="flex flex-col gap-2">
        <p className="text-ink-soft">{t('dashboard.notes.sessionsCreated')}</p>
        <p className="text-ink-soft">{t('dashboard.notes.proposals')}</p>
      </div>
    </Section>
  )
}

export function AdminDashboardPage() {
  const { t } = useTranslation(['admin', 'common'])
  const { number, percent, dateTime } = useAdminFormatters()
  const dashboard = useQuery({
    queryKey: adminKeys.dashboard,
    queryFn: getAdminDashboard,
    retry: false,
  })

  // D-177 fail-closed parity: `RequireAdmin` answers 404, indistinguishable
  // from a nonexistent page, so this page-local branch says only that — never
  // that an admin-only surface exists behind it.
  if (dashboard.isError && isApiError(dashboard.error) && dashboard.error.status === 404) {
    return (
      <main data-testid="screen-admin-dashboard">
        <PageScaffold width="form">
          <p className="text-ink" role="alert">
            {t('errors.codes.not_found')}
          </p>
        </PageScaffold>
      </main>
    )
  }

  return (
    <main data-testid="screen-admin-dashboard">
      <PageScaffold>
        <PageHeader
          actions={
            /*
              Q2's URL-only posture governs navigation outside the admin
              surface, not inside it: this link renders only on the admin
              dashboard, which non-admins never reach, so it leaks nothing.
            */
            <Link
              className={`inline-flex items-center justify-center bg-card px-4 py-2 text-ink ${raisedInteractiveClasses}`}
              to="/admin/users"
            >
              {t('dashboard.usersLink')}
            </Link>
          }
          subtitle={
            dashboard.data
              ? t('dashboard.generatedAt', {
                  timestamp: dateTime.format(new Date(dashboard.data.generatedAt)),
                })
              : undefined
          }
          title={t('dashboard.heading')}
        />

        {dashboard.isPending && (
          <p className="motion-safe:animate-pulse text-ink-soft" role="status">
            {t('dashboard.loading')}
          </p>
        )}

        {dashboard.isError && (
          <StatusBanner
            actions={
              <Button variant="neutral" onClick={() => void dashboard.refetch()}>
                {t('actions.retry', { ns: 'common' })}
              </Button>
            }
            role="alert"
            variant="danger"
          >
            {t(
              isApiError(dashboard.error)
                ? `errors.codes.${dashboard.error.code}`
                : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        {dashboard.data && (
          <>
            <UsageWindowPanel
              headingId="admin-window-7"
              title={t('dashboard.windows.last7Days')}
              window={dashboard.data.last7Days}
            />
            <UsageWindowPanel
              headingId="admin-window-30"
              title={t('dashboard.windows.last30Days')}
              window={dashboard.data.last30Days}
            />
            <Section
              description={t('dashboard.repeat.window', {
                days: numeric(dashboard.data.repeatScheduling.days),
              })}
              title={t('dashboard.repeat.heading')}
              titleId="admin-repeat"
            >
              {/*
                Design §3 S3: the raw organizer counts render beside the rate,
                never instead of it — recent cohorts structurally depress the
                rate, so the rate on its own is not readable.
              */}
              <dl className="grid gap-3 sm:grid-cols-3">
                <Metric
                  label={t('dashboard.repeat.rate')}
                  value={
                    optionalNumeric(dashboard.data.repeatScheduling.rate) === null
                      ? t('dashboard.noValue')
                      : percent.format(
                          optionalNumeric(dashboard.data.repeatScheduling.rate) ?? 0,
                        )
                  }
                  valueLabel={
                    optionalNumeric(dashboard.data.repeatScheduling.rate) === null
                      ? t('dashboard.noValueLabel')
                      : undefined
                  }
                />
                <Metric
                  label={t('dashboard.repeat.organizers')}
                  value={number.format(numeric(dashboard.data.repeatScheduling.organizers))}
                />
                <Metric
                  label={t('dashboard.repeat.repeatOrganizers')}
                  value={number.format(
                    numeric(dashboard.data.repeatScheduling.repeatOrganizers),
                  )}
                />
              </dl>
              <p className="text-ink-soft">{t('dashboard.notes.repeat')}</p>
            </Section>
          </>
        )}
      </PageScaffold>
    </main>
  )
}
