import { useQueries } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { getDiscoverSessions, homeKeys } from '../home/homeQueries'

/**
 * D-149's dedicated Discover surface — `scope=discover` on the shipped
 * `ListSessions` operation (A-072/A-077 §6.3). Every row links to the
 * shipped `/sessions/:id` detail route (D-114); Discover adds no second
 * detail surface.
 */
export function DiscoverPage() {
  const { t, i18n } = useTranslation(['home', 'sessions', 'errors', 'common'])
  const heading = useRef<HTMLHeadingElement>(null)

  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: homeKeys.discover(cursor),
      queryFn: () => getDiscoverSessions(cursor),
    })),
  })

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const items = pages.flatMap((page) => page.data?.items ?? [])
  const isPending = pages[0]?.isPending ?? true
  const error = pages.find((page) => page.error)?.error
  const lastPage = pages[pages.length - 1]
  const nextCursor = lastPage?.data?.nextCursor ?? undefined
  const isFetchingNextPage = pages.length > 1 && (lastPage?.isFetching ?? false)

  const timeFormat = new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

  return (
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-discover"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {t('discover.heading')}
      </h1>

      {isPending && (
        <p role="status" className="motion-safe:animate-pulse text-slate-500">
          {t('common.loading', { ns: 'common' })}
        </p>
      )}

      {error && (
        <p role="alert">
          {t(isApiError(error) ? `errors.codes.${error.code}` : 'errors.generic', {
            ns: 'sessions',
            defaultValue: t('errors.generic', { ns: 'sessions' }),
          })}
        </p>
      )}

      {!isPending && items.length === 0 && <p>{t('discover.empty')}</p>}

      {items.length > 0 && (
        <ul className="space-y-3">
          {items.map((session) => (
            <li key={session.id}>
              <Link
                className="block rounded-xl border border-slate-200 bg-white p-4 shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                data-viewer-relation={session.viewerRelation}
                to={`/sessions/${session.id}`}
              >
                <p className="font-semibold text-slate-950">{session.title}</p>
                <p className="text-sm text-slate-600">
                  {timeFormat.format(new Date(session.startTime))}
                </p>
                <p className="text-sm text-slate-600">
                  {t('discover.organizedBy', {
                    name: session.organizer.displayName ?? t('detail.anonymous', { ns: 'sessions' }),
                  })}
                </p>
              </Link>
            </li>
          ))}
        </ul>
      )}

      {nextCursor && (
        <Button
          aria-busy={isFetchingNextPage}
          className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          disabled={isFetchingNextPage}
          onClick={() => setCursors((current) => [...current, nextCursor])}
        >
          {t('discover.loadMore')}
        </Button>
      )}
    </main>
  )
}
