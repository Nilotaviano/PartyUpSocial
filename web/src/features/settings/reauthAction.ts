export type ReauthAction =
  | { kind: 'deleteAccount' }
  | { kind: 'linkAuthMethod'; provider: string }
  | { kind: 'unlinkAuthMethod'; method: string }
  | { kind: 'unlinkPlatform'; id: string }

const pendingActionStorageKey = 'partyup.pending-reauth-action'

function isReauthAction(value: unknown): value is ReauthAction {
  if (!value || typeof value !== 'object') return false
  const action = value as Partial<ReauthAction>
  return (
    action.kind === 'deleteAccount' ||
    (action.kind === 'linkAuthMethod' && typeof action.provider === 'string') ||
    (action.kind === 'unlinkAuthMethod' && typeof action.method === 'string') ||
    (action.kind === 'unlinkPlatform' && typeof action.id === 'string')
  )
}

export function setPendingSettingsAction(action: ReauthAction) {
  sessionStorage.setItem(pendingActionStorageKey, JSON.stringify(action))
}

export function getPendingSettingsAction() {
  const value = sessionStorage.getItem(pendingActionStorageKey)
  sessionStorage.removeItem(pendingActionStorageKey)
  if (!value) return null

  try {
    const action: unknown = JSON.parse(value)
    if (isReauthAction(action)) return action
  } catch {
    // Malformed storage is never a valid action.
  }

  return null
}

export function peekPendingSettingsAction() {
  const value = sessionStorage.getItem(pendingActionStorageKey)
  if (!value) return null

  try {
    const action: unknown = JSON.parse(value)
    return isReauthAction(action) ? action : null
  } catch {
    return null
  }
}

export function clearPendingSettingsAction() {
  sessionStorage.removeItem(pendingActionStorageKey)
}
