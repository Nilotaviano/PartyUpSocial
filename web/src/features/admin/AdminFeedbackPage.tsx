import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { fieldSelectClasses, quietChipClasses } from '../../components/ui/classes'
import { Label } from '../../components/ui/label'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import {
  adminFeedbackKeys,
  getAdminFeedback,
  invalidateAdminFeedback,
  setAdminFeedbackHandled,
  type AdminFeedbackItem,
  type FeedbackType,
} from '../feedback/feedbackQueries'
import { numeric } from './adminQueries'

type TypeFilter = FeedbackType | 'all'
type HandledFilter = 'all' | 'handled' | 'unhandled'

const typeFilters: TypeFilter[] = ['all', 'bug', 'idea', 'other']
const handledFilters: HandledFilter[] = ['all', 'unhandled', 'handled']

function ContextField({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-ink-soft">{label}</dt>
      <dd className="break-words font-body text-ink">{value}</dd>
    </div>
  )
}

function FeedbackRow({
  item,
  onToggle,
  pending,
}: {
  item: AdminFeedbackItem
  onToggle: (item: AdminFeedbackItem) => void
  pending: boolean
}) {
  const { t, i18n } = useTranslation('admin')
  const dateTime = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
  const none = t('feedback.context.none')
  const typeLabel = t(`feedback.types.${item.type}`, {
    defaultValue: t('feedback.types.unknown'),
  })

  return (
    <li className="flex flex-col gap-3 border-t border-ink/25 py-4 first:border-t-0">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className={quietChipClasses}>{typeLabel}</span>
          <span className={quietChipClasses}>
            {item.handled && item.handledAt !== null
              ? t('feedback.handledAt', { date: dateTime.format(new Date(item.handledAt)) })
              : t('feedback.notHandled')}
          </span>
        </div>
        <Button
          aria-busy={pending}
          disabled={pending}
          variant="neutral"
          onClick={() => onToggle(item)}
        >
          {item.handled ? t('feedback.markUnhandled') : t('feedback.markHandled')}
        </Button>
      </div>

      <p className="whitespace-pre-wrap break-words text-ink">{item.message}</p>

      <dl className="grid gap-2 sm:grid-cols-3">
        <ContextField
          label={t('feedback.submitter')}
          value={item.submitterUserId ?? t('feedback.anonymous')}
        />
        <ContextField
          label={t('feedback.submitted')}
          value={dateTime.format(new Date(item.createdAt))}
        />
        <ContextField label={t('feedback.context.route')} value={item.route ?? none} />
        <ContextField label={t('feedback.context.locale')} value={item.locale ?? none} />
        <ContextField label={t('feedback.context.appVersion')} value={item.appVersion ?? none} />
        <ContextField label={t('feedback.context.userAgent')} value={item.userAgent ?? none} />
      </dl>
    </li>
  )
}

/**
 * D-214's admin read surface. Newest-first ordering, paging and both filters are the server's
 * (`GET /admin/feedback`) — this page never re-sorts or re-filters what it was handed, so what an
 * admin sees is exactly the page the query described.
 */
export function AdminFeedbackPage() {
  const { t, i18n } = useTranslation(['admin', 'common'])
  const queryClient = useQueryClient()
  const [type, setType] = useState<TypeFilter>('all')
  const [handled, setHandled] = useState<HandledFilter>('all')
  const [page, setPage] = useState(1)
  const [pendingId, setPendingId] = useState<string | null>(null)

  const feedback = useQuery({
    queryKey: adminFeedbackKeys.list(type, handled, page),
    queryFn: () => getAdminFeedback(type, handled, page),
    placeholderData: keepPreviousData,
    retry: false,
  })

  const toggle = useMutation({
    mutationFn: (item: AdminFeedbackItem) => {
      setPendingId(item.id)
      return setAdminFeedbackHandled(item.id, !item.handled)
    },
    onSettled: async () => {
      setPendingId(null)
      await invalidateAdminFeedback(queryClient)
    },
  })

  // D-177 fail-closed parity — see AdminDashboardPage's own branch.
  if (feedback.isError && isApiError(feedback.error) && feedback.error.status === 404) {
    return (
      <main data-testid="screen-admin-feedback">
        <PageScaffold width="form">
          <p className="text-ink" role="alert">
            {t('errors.codes.not_found')}
          </p>
        </PageScaffold>
      </main>
    )
  }

  const number = new Intl.NumberFormat(i18n.language)
  const items = feedback.data?.items ?? []
  const total = feedback.data ? numeric(feedback.data.total) : 0
  const pageSize = feedback.data ? numeric(feedback.data.pageSize) : 0
  const hasNextPage = feedback.data ? page * pageSize < total : false

  return (
    <main data-testid="screen-admin-feedback">
      <PageScaffold>
        <PageHeader back={{ to: '/admin', label: t('feedback.back') }} title={t('feedback.heading')} />

        <div className="flex flex-wrap items-end gap-3">
          <div>
            <Label htmlFor="admin-feedback-type">{t('feedback.filters.type')}</Label>
            <select
              className={fieldSelectClasses}
              id="admin-feedback-type"
              value={type}
              onChange={(event) => {
                setPage(1)
                setType(event.target.value as TypeFilter)
              }}
            >
              {typeFilters.map((value) => (
                <option key={value} value={value}>
                  {value === 'all' ? t('feedback.filters.all') : t(`feedback.types.${value}`)}
                </option>
              ))}
            </select>
          </div>
          <div>
            <Label htmlFor="admin-feedback-handled">{t('feedback.filters.handled')}</Label>
            <select
              className={fieldSelectClasses}
              id="admin-feedback-handled"
              value={handled}
              onChange={(event) => {
                setPage(1)
                setHandled(event.target.value as HandledFilter)
              }}
            >
              {handledFilters.map((value) => (
                <option key={value} value={value}>
                  {t(
                    value === 'all'
                      ? 'feedback.filters.all'
                      : value === 'handled'
                        ? 'feedback.filters.handledOnly'
                        : 'feedback.filters.unhandledOnly',
                  )}
                </option>
              ))}
            </select>
          </div>
        </div>

        {feedback.isPending && (
          <p className="motion-safe:animate-pulse text-ink-soft" role="status">
            {t('feedback.loading')}
          </p>
        )}

        {feedback.isError && (
          <StatusBanner
            actions={
              <Button variant="neutral" onClick={() => void feedback.refetch()}>
                {t('actions.retry', { ns: 'common' })}
              </Button>
            }
            role="alert"
            variant="danger"
          >
            {t(
              isApiError(feedback.error) ? `errors.codes.${feedback.error.code}` : 'errors.generic',
              { defaultValue: t('errors.generic') },
            )}
          </StatusBanner>
        )}

        {feedback.data && (
          <p className="text-ink-soft" role="status">
            {t('feedback.total', {
              shown: number.format(items.length),
              total: number.format(total),
            })}
          </p>
        )}

        {feedback.data && items.length === 0 && (
          <p className="text-ink">{t('feedback.empty')}</p>
        )}

        {items.length > 0 && (
          <Section label={t('feedback.heading')}>
            <ul>
              {items.map((item) => (
                <FeedbackRow
                  key={item.id}
                  item={item}
                  pending={toggle.isPending && pendingId === item.id}
                  onToggle={(target) => toggle.mutate(target)}
                />
              ))}
            </ul>
          </Section>
        )}

        {feedback.data && (page > 1 || hasNextPage) && (
          <nav aria-label={t('feedback.pagination.page', { page })} className="flex gap-2">
            <Button
              disabled={page === 1}
              variant="neutral"
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            >
              {t('feedback.pagination.previous')}
            </Button>
            <Button
              disabled={!hasNextPage}
              variant="neutral"
              onClick={() => setPage((current) => current + 1)}
            >
              {t('feedback.pagination.next')}
            </Button>
          </nav>
        )}
      </PageScaffold>
    </main>
  )
}
