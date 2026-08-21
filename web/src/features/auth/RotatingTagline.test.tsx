import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import '../../i18n'
import i18n from '../../i18n'
import { ROTATE_INTERVAL_MS, RotatingTagline } from './RotatingTagline'

afterEach(async () => {
  cleanup()
  vi.useRealTimers()
  await i18n.changeLanguage('en')
})

describe('RotatingTagline', () => {
  it('keeps the full sentence as the accessible text and hides the rotating copy', () => {
    render(
      <h1>
        <RotatingTagline />
      </h1>,
    )
    expect(
      screen.getByRole('heading', {
        name: 'Find time to play with your friends.',
      }),
    ).toBeInTheDocument()
    expect(screen.getByTestId('tagline-rotating')).toHaveAttribute(
      'aria-hidden',
      'true',
    )
  })

  it('advances the verb on each interval and wraps around', () => {
    vi.useFakeTimers()
    render(<RotatingTagline />)
    const rotating = () => screen.getByTestId('tagline-rotating')
    expect(rotating()).toHaveTextContent('Find time to play with your friends.')

    act(() => vi.advanceTimersByTime(ROTATE_INTERVAL_MS))
    expect(rotating().querySelector('.rotating-word-out')).toHaveTextContent(
      'play',
    )
    expect(rotating().querySelector('.rotating-word-in')).toHaveTextContent(
      'meet up',
    )

    act(() => vi.advanceTimersByTime(ROTATE_INTERVAL_MS * 3))
    expect(rotating().querySelector('.rotating-word-in')).toHaveTextContent(
      'play',
    )
  })

  it('pauses while the document is hidden', () => {
    vi.useFakeTimers()
    render(<RotatingTagline />)
    const current = () =>
      screen.getByTestId('tagline-rotating').querySelector('.rotating-word-in')

    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    act(() => vi.advanceTimersByTime(ROTATE_INTERVAL_MS * 3))
    expect(current()).toHaveTextContent('play')

    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible')
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    act(() => vi.advanceTimersByTime(ROTATE_INTERVAL_MS))
    expect(current()).toHaveTextContent('meet up')
  })

  for (const [locale, verb] of [
    ['pt-BR', 'jogar'],
    ['es', 'jugar'],
  ] as const) {
    it(`${locale} renders its own verb list D-060`, async () => {
      await i18n.changeLanguage(locale)
      render(<RotatingTagline />)
      expect(
        screen
          .getByTestId('tagline-rotating')
          .querySelector('.rotating-word-in'),
      ).toHaveTextContent(verb)
    })
  }
})
