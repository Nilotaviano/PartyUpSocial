import { createContext, useContext } from 'react'

/**
 * The shell-mounted portal slot's wiring (STYLING_OVERHAUL_DESIGN.md §3.5/R3).
 * A shell component that must stay mounted across navigation (the push prompt)
 * renders where the shell puts it *unless* the current page offers a slot, in
 * which case it portals into the page column. A page with no slot — and every
 * provider-less component test — keeps the shell position, which is the
 * compatibility guarantee for unmigrated routes.
 *
 * The context lives apart from `pageScaffold.tsx` so that file keeps exporting
 * components only.
 */
export interface PageSlotRegistry {
  target: HTMLElement | null
  register: (node: HTMLElement | null) => void
}

export const PageSlotContext = createContext<PageSlotRegistry | null>(null)

export function usePageSlotRegistry(): PageSlotRegistry | null {
  return useContext(PageSlotContext)
}

/** The element the current page offers, or `null` when it offers none. */
export function usePageSlot(): HTMLElement | null {
  return usePageSlotRegistry()?.target ?? null
}
