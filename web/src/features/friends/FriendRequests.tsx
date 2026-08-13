import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { EmptyState } from '../../components/ui/emptyState'
import { IdentityRow } from '../../components/ui/identityRow'
import {
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendGraphAction,
  type FriendRequestItem,
} from './friendGraph'

type FriendRequestsProps = {
  direction: 'incoming' | 'outgoing'
  items: FriendRequestItem[]
  onStatus: (message: string) => void
}

export function FriendRequests({
  direction,
  items,
  onStatus,
}: FriendRequestsProps) {
  const { t } = useTranslation(['friends', 'errors'])
  const queryClient = useQueryClient()
  const action = useMutation({
    mutationFn: runFriendGraphAction,
    onSuccess: async (_result, input) => {
      const statusKey =
        input.kind === 'accept'
          ? 'status.requestAccepted'
          : input.kind === 'reject'
            ? 'status.requestRejected'
            : input.kind === 'cancel'
              ? 'status.requestCanceled'
              : null
      if (statusKey) onStatus(t(statusKey))
      await invalidateFriendGraph(queryClient, input)
    },
  })

  if (items.length === 0) {
    // An empty request queue is a good terminal state, so it takes the quiet
    // variant (STYLING_OVERHAUL_DESIGN.md §3.6) rather than a CTA.
    return (
      <EmptyState illustration="✉️" variant="quiet">
        {t(direction === 'incoming' ? 'empty.incoming' : 'empty.outgoing')}
      </EmptyState>
    )
  }

  function mutate(
    kind: Extract<FriendGraphAction['kind'], 'accept' | 'reject' | 'cancel'>,
    item: FriendRequestItem,
  ) {
    action.mutate({
      kind,
      username: item.username,
      friendshipId: item.friendshipId,
    })
  }

  return (
    <>
      <ul className="grid gap-3 sm:grid-cols-2">
        {items.map((request) => (
          <li key={request.friendshipId}>
            <article className="flex h-full flex-col justify-between gap-4 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
              {/* D-068/D-188: a pending request exposes the username and nothing else. */}
              <IdentityRow username={request.username} variant="handle" />
              <div className="flex flex-wrap gap-2">
                {direction === 'incoming' ? (
                  <>
                    <Button
                      aria-busy={action.isPending}
                      aria-label={`${t('actions.accept')} @${request.username}`}
                      disabled={action.isPending}
                      variant="primary"
                      onClick={() => mutate('accept', request)}
                    >
                      {t('actions.accept')}
                    </Button>
                    <Button
                      aria-busy={action.isPending}
                      aria-label={`${t('actions.reject')} @${request.username}`}
                      disabled={action.isPending}
                      variant="neutral"
                      onClick={() => mutate('reject', request)}
                    >
                      {t('actions.reject')}
                    </Button>
                  </>
                ) : (
                  <Button
                    aria-busy={action.isPending}
                    aria-label={`${t('actions.cancelRequest')} @${request.username}`}
                    disabled={action.isPending}
                    variant="neutral"
                    onClick={() => mutate('cancel', request)}
                  >
                    {t('actions.cancelRequest')}
                  </Button>
                )}
              </div>
            </article>
          </li>
        ))}
      </ul>
      {action.error && (
        <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
          {t(isApiError(action.error) ? action.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
    </>
  )
}
