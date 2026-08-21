import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

export const ROTATE_INTERVAL_MS = 2500

type Rotation = { index: number; previous: number | null }

/**
 * Hero tagline whose verb cycles ("play" → "meet up" → …). The full sentence
 * (`app.tagline`) stays as the accessible text; the rotating copy is
 * aria-hidden so screen readers hear one stable headline, not a tick every
 * 2.5 s. Rotation pauses while the tab is hidden.
 */
export function RotatingTagline() {
  const { t } = useTranslation()
  const verbs = t('app.taglineRotating.verbs', {
    returnObjects: true,
  }) as string[]
  const [rotation, setRotation] = useState<Rotation>({
    index: 0,
    previous: null,
  })

  useEffect(() => {
    if (verbs.length < 2) return
    let timer: ReturnType<typeof setInterval> | undefined
    const start = () => {
      if (timer !== undefined) return
      timer = setInterval(() => {
        setRotation(({ index }) => ({
          index: (index + 1) % verbs.length,
          previous: index,
        }))
      }, ROTATE_INTERVAL_MS)
    }
    const stop = () => {
      if (timer === undefined) return
      clearInterval(timer)
      timer = undefined
    }
    const onVisibility = () => {
      if (document.visibilityState === 'visible') start()
      else stop()
    }
    onVisibility()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      stop()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [verbs.length])

  return (
    <>
      <span className="sr-only">{t('app.tagline')}</span>
      <span aria-hidden data-testid="tagline-rotating">
        {t('app.taglineRotating.prefix')}
        <span className="rotating-word">
          {rotation.previous !== null && (
            <span
              key={`out-${rotation.previous}`}
              className="rotating-word-out"
            >
              {verbs[rotation.previous]}
            </span>
          )}
          <span key={`in-${rotation.index}`} className="rotating-word-in">
            {verbs[rotation.index]}
          </span>
        </span>
        {t('app.taglineRotating.suffix')}
      </span>
    </>
  )
}
