import type { ReauthAction } from './ReauthPrompt'

let pendingAction: ReauthAction | null = null

export function setPendingSettingsAction(action: ReauthAction) {
  pendingAction = action
}

export function consumePendingSettingsAction() {
  const current = pendingAction
  pendingAction = null
  return current
}