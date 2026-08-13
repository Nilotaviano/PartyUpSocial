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
  // Lifting the anchor above the scrim raises the element, not a backdrop: an
  // anchor with no background of its own (an inactive nav link) still shows the
  // scrim-darkened page through itself, so its own text keeps a light-mode ink
  // colour over a darkened ground — measured at 1.94:1 by the S7 mid-run scan.
  // An opaque pseudo-element behind the content restores the token pair the
  // anchor was designed against. It is a `::before` rather than a `bg-*`
  // utility because an anchor that DOES carry its own fill (an active nav link
  // is `bg-primary`) must keep it — two background utilities on one element
  // would resolve by Tailwind's emit order, not by intent.
  'before:absolute',
  'before:inset-0',
  'before:-z-10',
  'before:rounded-control',
  'before:bg-card',
  "before:content-['']",
]

/**
 * Card layout constants (§3: below the anchor, clamped to the viewport, arrow points at it).
 * CARD_WIDTH_PX and CARD_MARGIN_PX must match the card's Tailwind classes
 * (max-w-[320px] w-[calc(100%_-_2rem)]) — the arrow is aimed with this arithmetic,
 * so changing the classes without these constants silently de-points it. CARD_MAX_HEIGHT_PX is
 * the height the above/below flip decision below plans against, and matches the card's
 * `max-h-[min(280px,calc(100dvh-2rem))]` — the `calc(100dvh-2rem)` half is load-bearing, not
 * decorative: without it CSS could render the card taller than this constant on a short viewport,
 * which would silently invalidate the flip math's own height assumption (`overflow-y-auto` only
 * guards genuinely-long content, not this).
 */
const CARD_WIDTH_PX = 320
const CARD_MAX_HEIGHT_PX = 280
const CARD_MARGIN_PX = 16
const CARD_GAP_PX = 12
const ARROW_HALF_PX = 8

type ReplayState = { replayTour?: boolean } | null

type CardPosition = { top: number; left: number; arrowLeft: number; arrowAtBottom: boolean }

/**
 * Computes the card's viewport-fixed `{top, left}` near the active anchor and the arrow's left
 * offset pointing back at the anchor's center. Horizontally: the nav is a row, so a clamped
 * center-under-anchor offset always has room. Vertically: below the anchor by default (every
 * shipped anchor lives in the top header, so this is the common case). It flips above the anchor
 * (`arrowAtBottom`) ONLY when the above position genuinely clears the viewport's top margin on
 * its own — a flip that would itself need clamping back down draws the card over the very anchor
 * it spotlights (a short landscape-phone height clamping the flipped top to `CARD_MARGIN_PX`,
 * which sits on top of a top-header anchor, is exactly that case) — so on a viewport too short for
 * either to fit cleanly, this stays below and clamped, never flips into the anchor.
 */
function computeCardPosition(anchorRect: DOMRect): CardPosition {
  const cardWidth = Math.min(CARD_WIDTH_PX, window.innerWidth - CARD_MARGIN_PX * 2)
  const anchorCenter = anchorRect.left + anchorRect.width / 2
  const maxLeft = Math.max(window.innerWidth - cardWidth - CARD_MARGIN_PX, CARD_MARGIN_PX)
  const left = Math.min(Math.max(anchorCenter - cardWidth / 2, CARD_MARGIN_PX), maxLeft)

  const cardHeight = Math.min(CARD_MAX_HEIGHT_PX, window.innerHeight - CARD_MARGIN_PX * 2)
  const belowTop = anchorRect.bottom + CARD_GAP_PX
  const fitsBelow = belowTop + cardHeight <= window.innerHeight - CARD_MARGIN_PX
  const aboveTop = anchorRect.top - CARD_GAP_PX - cardHeight
  const canFlipAbove = !fitsBelow && aboveTop >= CARD_MARGIN_PX
  const top = canFlipAbove ? aboveTop : Math.min(belowTop, window.innerHeight - CARD_MARGIN_PX)

  const arrowLeft = Math.min(
    Math.max(anchorCenter - left, ARROW_HALF_PX * 2),
    cardWidth - ARROW_HALF_PX * 2,
  )
  return { top, left, arrowLeft, arrowAtBottom: canFlipAbove }
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

    // Coalesces however many scroll/resize events land in one frame into a single recompute —
    // smooth-scrolling `scrollIntoView` fires `scroll` on essentially every frame, and without
    // this a `setCardPosition` (and the dialog re-render it triggers) would run once per event
    // instead of once per paint.
    let pendingFrame: number | null = null
    function scheduleReposition() {
      if (pendingFrame !== null) return
      pendingFrame = window.requestAnimationFrame(() => {
        pendingFrame = null
        reposition()
      })
    }

    const reducedMotion =
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(prefers-reduced-motion: reduce)').matches
    element.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'nearest', inline: 'nearest' })
    element.classList.add(...SPOTLIGHT_CLASSES)
    queueMicrotask(reposition)

    // Tracks the anchor through the scroll-into-view animation and any viewport resize — the card
    // and its arrow stay pinned to the anchor rather than freezing at the pre-scroll position.
    window.addEventListener('resize', scheduleReposition)
    window.addEventListener('scroll', scheduleReposition, true)
    return () => {
      element.classList.remove(...SPOTLIGHT_CLASSES)
      window.removeEventListener('resize', scheduleReposition)
      window.removeEventListener('scroll', scheduleReposition, true)
      if (pendingFrame !== null) {
        window.cancelAnimationFrame(pendingFrame)
      }
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
          className="fixed z-50 w-[calc(100%_-_2rem)] max-w-[320px] max-h-[min(280px,calc(100dvh-2rem))] overflow-y-auto rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog motion-safe:transition-[top,left] motion-safe:duration-200 motion-safe:ease-out focus:outline-none"
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
          {/* §3's card arrow: a rotated square whose visible border edges read as a caret pointing
              at the spotlit anchor — top-left edges when the card sits below the anchor (the
              common case, every shipped anchor lives in the header), bottom-right edges when
              `computeCardPosition` had to flip the card above it instead. Purely decorative
              (aria-hidden) — the ring on the anchor itself and Dialog.Title/Description carry the
              real semantics. */}
          <div
            aria-hidden="true"
            className={
              cardPosition?.arrowAtBottom
                ? 'absolute -bottom-[9px] h-4 w-4 rotate-45 rounded-[2px] border-b-2 border-r-2 border-ink bg-card'
                : 'absolute -top-[9px] h-4 w-4 rotate-45 rounded-[2px] border-l-2 border-t-2 border-ink bg-card'
            }
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
