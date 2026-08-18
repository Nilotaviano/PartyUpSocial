import { afterEach, describe, expect, it, vi } from 'vitest'

import { registerServiceWorker } from './registerServiceWorker'

function mockServiceWorkerContainer(register: () => Promise<unknown>) {
  Object.defineProperty(navigator, 'serviceWorker', {
    configurable: true,
    value: { register },
  })
}

function removeServiceWorkerContainer() {
  Reflect.deleteProperty(navigator, 'serviceWorker')
}

afterEach(() => {
  removeServiceWorkerContainer()
  vi.restoreAllMocks()
})

describe('registerServiceWorker (D-218)', () => {
  it('registers the shell worker at /sw.js_D218', () => {
    const register = vi.fn().mockResolvedValue({})
    mockServiceWorkerContainer(register)

    registerServiceWorker()

    expect(register).toHaveBeenCalledWith('/sw.js')
  })

  it('swallows a rejected registration instead of surfacing an unhandled rejection_D218', async () => {
    const register = vi.fn().mockRejectedValue(new Error('no worker here'))
    mockServiceWorkerContainer(register)

    expect(() => {
      registerServiceWorker()
    }).not.toThrow()

    await expect(register.mock.results[0]?.value).rejects.toThrow(
      'no worker here',
    )
  })

  it('does nothing on a browser with no service-worker support_D218', () => {
    removeServiceWorkerContainer()

    expect(() => {
      registerServiceWorker()
    }).not.toThrow()
    expect('serviceWorker' in navigator).toBe(false)
  })
})
