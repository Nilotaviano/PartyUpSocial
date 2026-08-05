import { useQueries, useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  getSessionList,
  getViewerProfile,
  isSessionInProgress,
  sessionKeys,
  viewerProfileKey,
  type SessionScope,
} from './sessionQueries'

type SessionListPageProps = {
  scope: SessionScope
}

export function SessionListPage({ scope }: SessionListPageProps) {
  const { t, i18n } = useTranslation(['sessions', 'errors', 'common'])
  const heading = useRef<HTMLHeadingElement>(null)

  // D-038/D-046: times render in the viewer's profile time zone, not the
  // browser's, falling back to the browser zone only until the profile
  // query resolves.
  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  })
  const viewerTimeZone = profile.data?.timeZone ?? undefined

  // Each scope is a distinct route in App.tsx, so react-router mounts a
  // fresh SessionListPage instance per scope: cursors always starts at the
  // first page for a given mounted instance, with no reset-on-prop effect
  // needed. Cursors grow only from an explicit "load more" click, and every
  // requested page keeps the exact ['sessions', scope, cursor] key (§10.1).
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: sessionKeys.list(scope, cursor),
      queryFn: () => getSessionList(scope, cursor),
    })),
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const headingText = scope === 'upcoming' ? t('list.upcoming') : t('list.history')
  const emptyText = scope === 'upcoming' ? t('list.emptyUpcoming') : t('list.emptyHistory')
  const items = pages.flatMap((page) => page.data?.items ?? [])
  const isPending = pages[0]?.isPending ?? true
  const error = pages.find((page) => page.error)?.error
  const lastPage = pages[pages.length - 1]
  const nextCursor = lastPage?.data?.nextCursor ?? undefined
  const isFetchingNextPage = pages.length > 1 && (lastPage?.isFetching ?? false)

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid={`screen-sessions-${scope}`}
    >
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1
          ref={heading}
          tabIndex={-1}
          className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
        >
          {headingText}
        </h1>
        {scope === 'upcoming' && (
          <Link
            className="min-h-11 inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            to="/sessions/new"
          >
            {t('create.heading')}
          </Link>
        )}
      </header>

      {isPending && (
        <p role="status" className="motion-safe:animate-pulse text-slate-500">
          {t('common.loading', { ns: 'common' })}
        </p>
      )}

      {error && (
        <p role="alert">
          {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
            defaultValue: t('errors.generic'),
          })}
        </p>
      )}

      {!isPending && items.length === 0 && <p>{emptyText}</p>}

      {items.length > 0 && (
        <ul className="space-y-3">
          {items.map((session) => {
            const inProgress = scope === 'upcoming' && isSessionInProgress(session)
            return (
              <li key={session.id}>
                <Link
                  className="block rounded-xl border border-slate-200 bg-white p-4 shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                  to={`/sessions/${session.id}`}
                >
                  <p className="font-semibold text-slate-950">
                    {session.title}
                    {inProgress && (
                      <span className="ml-2 rounded-full bg-emerald-100 px-2 py-0.5 text-xs font-semibold text-emerald-800">
                        {t('list.inProgress')}
                      </span>
                    )}
                  </p>
                  <p className="text-sm text-slate-600">
                    {new Intl.DateTimeFormat(i18n.language, {
                      dateStyle: 'medium',
                      timeStyle: 'short',
                      timeZone: viewerTimeZone,
                    }).format(new Date(session.startTime))}
                  </p>
                </Link>
              </li>
            )
          })}
        </ul>
      )}

      {nextCursor && (
        <Button
          aria-busy={isFetchingNextPage}
          className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          disabled={isFetchingNextPage}
          onClick={() => setCursors((current) => [...current, nextCursor])}
        >
          {t('list.loadMore')}
        </Button>
      )}
    </main>
  )
}
