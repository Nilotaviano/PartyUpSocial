import { useState } from 'react'

/**
 * D-050 avatar disc (PHASE5_1_DESIGN.md §3): the emoji or linked-source
 * image renders on a disc whose fill rotates among free/accent/card by a
 * stable per-identity index, each with its `-ink` companion for the shipped
 * neutral `'●'` placeholder. Blocked/masked entries use this same treatment
 * unmodified — D-076 posture is visual ordinariness, not a distinct style.
 *
 * AVATARS_DESIGN.md §3.5: the `<img>` arm lives inside the existing
 * `aria-hidden` span, so a picture avatar stays decoration exactly like the
 * glyph it replaces — never a control's accessible name. `onError` falls
 * back to the glyph placeholder rather than the browser's broken-image icon:
 * without it, a transient proxy failure would render nothing recognizable,
 * which reads as the masked `'●'` treatment for an identity that is not
 * actually masked.
 */

const FILL_VARIANTS = ['free', 'accent', 'card'] as const
type FillVariant = (typeof FILL_VARIANTS)[number]

const FILL_CLASSES: Record<FillVariant, string> = {
  free: 'border-ink bg-free text-free-ink',
  accent: 'border-ink bg-accent text-accent-ink dark:border-accent-ink',
  card: 'border-ink bg-card text-ink',
}

const SIZE_CLASSES = {
  xs: 'h-9 w-9 text-lg',
  sm: 'h-12 w-12 text-xl',
  md: 'h-14 w-14 text-2xl',
} as const

function stableFillVariant(seed: string): FillVariant {
  let hash = 0
  for (let index = 0; index < seed.length; index += 1) {
    hash = (hash * 31 + seed.charCodeAt(index)) | 0
  }
  return FILL_VARIANTS[Math.abs(hash) % FILL_VARIANTS.length]
}

type AvatarDiscProps = {
  seed: string
  emoji?: string | null
  /** AVATARS_DESIGN.md §3.5: the proxy URL (`AvatarResponse.imgUrl`/`AvatarSourceOptionResponse.imgUrl`), never an upstream one (D-191). */
  imgUrl?: string | null
  size?: keyof typeof SIZE_CLASSES
  className?: string
  /**
   * D-076 posture: "Anonymous User" masking renders in the busy pair —
   * deliberately ordinary, never a distinct style. Forces the busy
   * fill/ring and the neutral `'●'` placeholder regardless of `emoji`/
   * `imgUrl`, so a masked identity never leaks its real avatar. The ordinary
   * treatment exists ahead of the first masked roster call site.
   */
  masked?: boolean
}

export function AvatarDisc({
  seed,
  emoji,
  imgUrl,
  size = 'md',
  className,
  masked = false,
}: AvatarDiscProps) {
  const [imgFailed, setImgFailed] = useState(false)
  const variant = stableFillVariant(seed)
  const showImg = !masked && !!imgUrl && !imgFailed
  const classes = [
    'grid shrink-0 place-items-center overflow-hidden rounded-full border-2 font-display',
    SIZE_CLASSES[size],
    masked ? 'border-ink bg-busy text-busy-ink' : FILL_CLASSES[variant],
    className,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <span aria-hidden="true" className={classes}>
      {showImg ? (
        <img
          alt=""
          className="h-full w-full object-cover"
          src={imgUrl}
          onError={() => setImgFailed(true)}
        />
      ) : (
        (masked ? '●' : (emoji ?? '●'))
      )}
    </span>
  )
}
