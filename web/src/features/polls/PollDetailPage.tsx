import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useParams } from 'react-router'
import { isApiError } from '../../api/client'
import { getPollDetail, isPollAwaitingSelection, pollKeys, type PollOption } from './pollQueries'
import { PollStatusChip } from './PollStatusChip'

const voteLabelKeys: Record<string, string> = {
  available: 'vote.available',
  maybe: 'vote.maybe',
  unavailable: 'vote.unavailable',
}

function optionTallyText(
  option: PollOption,
  t: (key: string, options?: Record<string, unknown>) => string,
) {
  const parts: string[] = []
  const available = Number(option.availableCount)
  const maybe = Number(option.maybeCount)
  const unavailable = Number(option.unavailableCount)
  if (available > 0) parts.push(t('detail.tallyAvailable', { count: available }))
  if (maybe > 0) parts.push(t('detail.tallyMaybe', { count: maybe }))
  if (unavailable > 0) parts.push(t('detail.tallyUnavailable', { count: unavailable }))
  return parts.length > 0 ? parts.join(' · ') : t('detail.tallyNone')
}

/**
 * Read-only poll detail (S5 scope): options, tallies, roster and viewer's
 * own vote render as static text — the interactive voting radiogroups and
 * suggest affordance are S6 (design §5). No mutations happen here.
 */
export function PollDetailPage() {
  const { t, i18n } = useTranslation(['polls', 'errors', 'common'])
  const { pollId = '' } = useParams()
  const heading = useRef<HTMLHeadingElement>(null)

  const detail = useQuery({
    queryKey: pollKeys.detail(pollId),
    queryFn: () => getPollDetail(pollId),
    enabled: Boolean(pollId),
    retry: false,
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  if (detail.isError && isApiError(detail.error) && detail.error.status === 404) {
    return <Navigate replace to="/polls" />
  }

  const data = detail.data
  const awaitingSelection = data ? isPollAwaitingSelection(data) : false
  const deadlineText = data
    ? new Intl.DateTimeFormat(i18n.language, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(data.deadline))
    : ''

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-poll-detail"
    >
      <h1
        ref={heading}
        aria-live="polite"
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {data?.title ?? t('common.loading', { ns: 'common' })}
      </h1>

      {detail.error && (
        <p role="alert">
          {t(
            isApiError(detail.error) ? `errors.codes.${detail.error.code}` : 'errors.generic',
            { defaultValue: t('errors.generic') },
          )}
        </p>
      )}

      {data && (
        <>
          <p className="flex flex-wrap items-center gap-2 text-ink">
            <PollStatusChip poll={data} />
          </p>

          {data.notes && <p className="text-ink">{data.notes}</p>}

          {data.status === 'polling' && !awaitingSelection && (
            <p className="text-ink-soft">{t('detail.deadline', { deadline: deadlineText })}</p>
          )}

          {awaitingSelection && (
            <p role="status">{t('detail.awaitingSelectionNotice')}</p>
          )}

          {data.status === 'finalized' && (
            <p role="status">
              {data.createdSessionId ? (
                <>
                  {t('detail.finalizedNotice')}{' '}
                  <Link
                    className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                    to={`/sessions/${data.createdSessionId}`}
                  >
                    {t('detail.viewSession')}
                  </Link>
                </>
              ) : (
                t('detail.finalizedNotice')
              )}
            </p>
          )}

          {data.status === 'expired' && <p role="status">{t('detail.expiredNotice')}</p>}
          {data.status === 'canceled' && <p role="status">{t('detail.canceledNotice')}</p>}

          <section aria-labelledby="poll-options-heading">
            <h2
              id="poll-options-heading"
              className="text-h2 font-display font-semibold text-ink"
            >
              {t('detail.options')}
            </h2>
            <ul className="mt-2 space-y-2">
              {data.options.map((option) => (
                <li
                  key={option.id}
                  className="rounded-card border-2 border-ink bg-card p-3"
                  data-testid={`poll-option-${option.id}`}
                >
                  <p className="font-display font-semibold text-ink">
                    {new Intl.DateTimeFormat(i18n.language, {
                      dateStyle: 'medium',
                      timeStyle: 'short',
                    }).format(new Date(option.startTime))}
                  </p>
                  <p className="text-ink-soft">{optionTallyText(option, t)}</p>
                  <p className="text-ink-soft">
                    {option.viewerVote
                      ? t('detail.yourVote', {
                          value: t(voteLabelKeys[option.viewerVote] ?? 'vote.unavailable'),
                        })
                      : t('detail.noVoteYet')}
                  </p>
                </li>
              ))}
            </ul>
          </section>

          <section aria-label={t('a11y.rosterLabel')} aria-labelledby="poll-roster-heading">
            <h2
              id="poll-roster-heading"
              className="text-h2 font-display font-semibold text-ink"
            >
              {t('detail.roster')}
            </h2>
            <ul className="mt-2">
              {data.roster.map((entry) => (
                <li
                  key={entry.participationId}
                  className="flex items-center justify-between gap-2 py-1"
                  data-testid={`poll-roster-entry-${entry.participationId}`}
                >
                  <span className="text-ink">
                    {entry.displayName ?? t('detail.anonymous')}
                  </span>
                  {!entry.isActive && (
                    <span className="text-ink-soft">{t('detail.removed')}</span>
                  )}
                </li>
              ))}
            </ul>
          </section>
        </>
      )}
    </main>
  )
}
