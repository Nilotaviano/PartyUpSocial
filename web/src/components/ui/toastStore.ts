import { createContext, useContext } from 'react'

/**
 * The dispatcher half of the shell-owned toast queue
 * (STYLING_OVERHAUL_DESIGN.md §3.5). Components request a toast; they never
 * render `Toast.Root` themselves. When no store is mounted above them — every
 * provider-less component test — the dispatcher is a no-op, so a toast can
 * never change what a test tree renders or throws.
 */
export interface ToastRequest {
  message: string
  action?: { label: string; onAction: () => void }
}

export type ToastDispatcher = (toast: ToastRequest) => void

const noop: ToastDispatcher = () => {}

export const ToastDispatchContext = createContext<ToastDispatcher | null>(null)

export function useToast(): ToastDispatcher {
  return useContext(ToastDispatchContext) ?? noop
}
