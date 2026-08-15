import { describe, expect, it } from 'vitest'
import { NAV_ITEMS, isNavItemActive } from './navItems'

describe('NAV_ITEMS — five chrome sections, no Discover (owner ruling)', () => {
  it('has exactly five items in the ruled order', () => {
    expect(NAV_ITEMS.map((item) => item.to)).toEqual([
      '/app',
      '/friends',
      '/sessions/upcoming',
      '/polls',
      '/calendar',
    ])
  })

  it('never lists Discover', () => {
    expect(NAV_ITEMS.some((item) => item.to.includes('discover'))).toBe(false)
  })
})

describe('isNavItemActive — Sessions activates across /sessions/*', () => {
  const sessions = NAV_ITEMS.find((item) => item.to === '/sessions/upcoming')!
  const polls = NAV_ITEMS.find((item) => item.to === '/polls')!
  const home = NAV_ITEMS.find((item) => item.to === '/app')!

  it('activates Sessions for its own link target', () => {
    expect(isNavItemActive('/sessions/upcoming', sessions)).toBe(true)
  })

  it('activates Sessions for a detail page outside the link target', () => {
    expect(isNavItemActive('/sessions/00000000-0000-0000-0000-000000000001', sessions)).toBe(true)
  })

  it('activates Sessions for the history tab', () => {
    expect(isNavItemActive('/sessions/history', sessions)).toBe(true)
  })

  it('does not activate Sessions for an unrelated route', () => {
    expect(isNavItemActive('/friends', sessions)).toBe(false)
  })

  it('activates Polls for its detail route', () => {
    expect(isNavItemActive('/polls/00000000-0000-0000-0000-000000000002', polls)).toBe(true)
  })

  it('activates Home only on its exact route', () => {
    expect(isNavItemActive('/app', home)).toBe(true)
    expect(isNavItemActive('/app/anything', home)).toBe(false)
  })
})
