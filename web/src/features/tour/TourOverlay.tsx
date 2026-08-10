import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router'
import { client } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'
import { TOUR_STEP_COUNT, TOUR_STEP_IDS } from './tourSteps'

/**
 * TOUR_DESIGN.md §3: the spotlit anchor is lifted above the scrim (z-index) and ringed in the
 * `outline-focus` token — never color alone (ring + position + card arrow). This exact class list
 * is a literal here (never built from interpolated fragments) so Tailwind's JIT scanner sees every
 * utility name.
 */
const SPOTLIGHT_CLASSES = [
  'relative',
  'z-50',
  'rounded-control',
  'outline',
  'outline-2',
  'outline-offset-2',
  'outline-focus',
]

/**
 * Card layout constants (§3: below the anchor, clamped to the viewport, arrow points at it).
 * CARD_WIDTH_PX and CARD_MARGIN_PX must match the card's Tailwind classes
 * (max-w-[320px] w-[calc(100%_-_2rem)]) — the arrow is aimed with this arithmetic,
 * so changing the classes without these constants silently de-points it.
 */
const CARD_WIDTH_PX = 320
const CARD_MARGIN_PX = 16
const CARD_GAP_PX = 12
const ARROW_HALF_PX = 8

type ReplayState = { replayTour?: boolean } | null

type CardPosition = { top: number; left: number; arrowLeft: number }

/**
 * Computes the card's viewport-fixed `{top, left}` below the active anchor (the nav is a
 * horizontal row, so below-with-clamped-horizontal-offset always has room) and the arrow's
 * left offset within the card pointing back at the anchor's center.
 */
function computeCardPosition(anchorRect: DOMRect): CardPosition {
  const cardWidth = Math.min(CARD_WIDTH_PX, window.innerWidth - CARD_MARGIN_PX * 2)
  const anchorCenter = anchorRect.left + anchorRect.width / 2
  const maxLeft = Math.max(window.innerWidth - cardWidth - CARD_MARGIN_PX, CARD_MARGIN_PX)
  const left = Math.min(Math.max(anchorCenter - cardWidth / 2, CARD_MARGIN_PX), maxLeft)
  const top = Math.min(anchorRect.bottom + CARD_GAP_PX, window.innerHeight - CARD_MARGIN_PX)
  const arrowLeft = Math.min(
    Math.max(anchorCenter - left, ARROW_HALF_PX * 2),
    cardWidth - ARROW_HALF_PX * 2,
  )
  return { top, left, arrowLeft }
}

/**
 * TOUR_DESIGN.md §1/§3: mounted once in `AuthenticatedShell`, rendering only on `/app`. The §1 gate
 * — profile query succeeded AND `tourCompleted` is false — is fail-closed by construction: a
 * pending or errored profile query never satisfies `status === 'success'`, so `App.test.tsx`'s
 * unmocked-`/api/v1/me` arms never see the tour open.
 * <p>
 * The once-per-page-load bound is `dismissedRef`, never the query cache — the default
 * `QueryClient` refetches `['my-profile']` on window focus (`NotificationBell.tsx:9-11`'s own
 * documented behavior), so a failed completion POST + refocus would otherwise re-derive an open
 * tour from data alone. The ref survives client-side navigation (this component stays mounted
 * across `/app`'s siblings inside `AuthenticatedShell`) and only resets on a real page load or on
 * Replay.
 */
export function TourOverlay() {
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { t } = useTranslation('tour')
  const dismissedRef = useRef(false)
  const [isOpen, setIsOpen] = useState(false)
  const [stepIndex, setStepIndex] = useState(0)
  const [cardPosition, setCardPosition] = useState<CardPosition | null>(null)

  const onAppRoute = location.pathname === '/app'
  const replayRequested = Boolean((location.state as ReplayState)?.replayTour)

  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
    enabled: onAppRoute,
  })

  useEffect(() => {
    if (!onAppRoute) {
      queueMicrotask(() => setIsOpen(false))
      return
    }

    if (replayRequested) {
      // TOUR_DESIGN.md §1: Replay clears the dismissed ref (without that, a user who skipped the
      // auto-tour and SPA-navigated here in the same page load would click a dead button) and
      // consumes the router state router-natively — `history.replaceState` directly would clobber
      // react-router's own state bookkeeping — so a reload does not re-trigger.
      dismissedRef.current = false
      queueMicrotask(() => {
        setStepIndex(0)
        setIsOpen(true)
      })
      navigate(location.pathname, { replace: true, state: null })
      return
    }

    if (!dismissedRef.current && profile.status === 'success' && profile.data?.tourCompleted === false) {
      queueMicrotask(() => {
        setStepIndex(0)
        setIsOpen(true)
      })
    }
  }, [onAppRoute, replayRequested, profile.status, profile.data?.tourCompleted, navigate, location.pathname])

  useEffect(() => {
    if (!isOpen) {
      queueMicrotask(() => setCardPosition(null))
      return
    }

    const anchorId = TOUR_STEP_IDS[stepIndex]
    const element = document.querySelector<HTMLElement>(`[data-tour="${anchorId}"]`)
    if (!element) {
      queueMicrotask(() => setCardPosition(null))
      return
    }

    function reposition() {
      setCardPosition(computeCardPosition(element!.getBoundingClientRect()))
    }

    const reducedMotion =
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(prefers-reduced-motion: reduce)').matches
    element.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'nearest', inline: 'nearest' })
    element.classList.add(...SPOTLIGHT_CLASSES)
    queueMicrotask(reposition)

    // Tracks the anchor through the scroll-into-view animation and any viewport resize — the card
    // and its arrow stay pinned to the anchor rather than freezing at the pre-scroll position.
    window.addEventListener('resize', reposition)
    window.addEventListener('scroll', reposition, true)
    return () => {
      element.classList.remove(...SPOTLIGHT_CLASSES)
      window.removeEventListener('resize', reposition)
      window.removeEventListener('scroll', reposition, true)
    }
  }, [isOpen, stepIndex])

  async function completeTour() {
    dismissedRef.current = true
    setIsOpen(false)
    queryClient.setQueryData(viewerProfileKey, (previous: typeof profile.data) =>
      previous ? { ...previous, tourCompleted: true } : previous,
    )
    try {
      await client.POST('/api/v1/me/tour-completed', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
      })
    } catch {
      // TOUR_DESIGN.md §1's accepted residual: a failed POST leaves the account-side flag false,
      // so a refocus-triggered refetch would re-derive an open tour from data alone were it not for
      // dismissedRef, which already suppresses re-open for the rest of this page load; the next
      // full load retries.
    }
  }

  if (!onAppRoute) {
    return null
  }

  const stepId = TOUR_STEP_IDS[stepIndex]
  const stepNumber = stepIndex + 1
  const isLastStep = stepIndex === TOUR_STEP_COUNT - 1
  // Centered fallback for the one render before the position effect measures the anchor (§3 still
  // reads as "near the anchor" once cardPosition lands a moment later under motion-safe:transition).
  const cardStyle = cardPosition
    ? { top: cardPosition.top, left: cardPosition.left }
    : { top: 96, left: '50%', transform: 'translateX(-50%)' }

  return (
    <Dialog.Root
      open={isOpen}
      onOpenChange={(next) => {
        // Esc is the only path that can reach here with `next === false` (`onInteractOutside` is
        // prevented below, and Skip/Finish call `completeTour` directly) — TOUR_DESIGN.md §3's
        // "Esc stays at its default (skips)".
        if (!next) {
          void completeTour()
        }
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-scrim" data-testid="tour-overlay-backdrop" />
        <Dialog.Content
          aria-describedby={`tour-step-${stepId}-description`}
          className="fixed z-50 w-[calc(100%_-_2rem)] max-w-[320px] rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog motion-safe:transition-[top,left] motion-safe:duration-200 motion-safe:ease-out focus:outline-none"
          data-testid="tour-overlay"
          style={cardStyle}
          onCloseAutoFocus={(event) => {
            // TOUR_DESIGN.md §3: no trigger element exists to restore focus to (the tour auto-opens
            // or opens via a full navigation from Replay), so focus goes to the Home link instead.
            event.preventDefault()
            document.querySelector<HTMLElement>('[data-tour="home"]')?.focus()
          }}
          onInteractOutside={(event) => event.preventDefault()}
        >
          {/* §3's card arrow: a rotated square whose top-left border edges read as a caret pointing
              up at the spotlit anchor above the card. Purely decorative (aria-hidden) — the ring on
              the anchor itself and Dialog.Title/Description carry the real semantics. */}
          <div
            aria-hidden="true"
            className="absolute -top-[9px] h-4 w-4 rotate-45 rounded-[2px] border-l-2 border-t-2 border-ink bg-card"
            style={{ left: cardPosition ? cardPosition.arrowLeft - ARROW_HALF_PX : undefined }}
          />
          <div aria-live="polite">
            <Dialog.Title className="font-display text-h2 font-semibold text-ink">
              {t('dialog.title', { step: stepNumber, total: TOUR_STEP_COUNT, name: t(`steps.${stepId}.name`) })}
            </Dialog.Title>
            <Dialog.Description
              className="mt-2 text-ink-soft"
              id={`tour-step-${stepId}-description`}
            >
              {t(`steps.${stepId}.body`)}
            </Dialog.Description>
          </div>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Button variant="ghost" onClick={() => void completeTour()}>
              {t('actions.skip')}
            </Button>
            {isLastStep ? (
              <Button variant="primary" onClick={() => void completeTour()}>
                {t('actions.finish')}
              </Button>
            ) : (
              <Button variant="primary" onClick={() => setStepIndex((index) => index + 1)}>
                {t('actions.next')}
              </Button>
            )}
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
