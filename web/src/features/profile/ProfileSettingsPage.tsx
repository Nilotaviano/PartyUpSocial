import { useEffect, useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { ReauthPrompt, type ReauthAction } from '../settings/ReauthPrompt'
import { clearPendingSettingsAction, getPendingSettingsAction } from '../settings/reauthAction'
import { SettingsNavigation } from '../settings/BlockedUsersPage'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'

const profileQueryKey = ['my-profile'] as const
const methodsQueryKey = ['auth-methods'] as const

type ProfileEndpoint = '/api/v1/me/username' | '/api/v1/me/display-name' | '/api/v1/me/avatar' | '/api/v1/me/time-zone'

async function getProfile() {
  const { data } = await client.GET('/api/v1/me')
  if (!data) throw new Error('Profile response was empty.')
  return data
}

async function getAuthMethods() {
  const { data } = await client.GET('/api/v1/auth/methods')
  if (!data) throw new Error('Authentication methods response was empty.')
  return data.methods
}

function ProfileError({ error }: { error: unknown }) {
  const { t } = useTranslation('errors')
  if (!error) return null
  const key = isApiError(error) && error.code === 'username_cooldown' ? 'username.cooldown' : 'generic'
  return <p role="alert">{t(key)}</p>
}

export function ProfileSettingsPage() {
  const { t } = useTranslation('profile')
  const queryClient = useQueryClient()
  const { data: profile, isPending, error } = useQuery({ queryKey: profileQueryKey, queryFn: getProfile })
  const { data: methods = [] } = useQuery({ queryKey: methodsQueryKey, queryFn: getAuthMethods })
  const [formError, setFormError] = useState<unknown>()
  const [saving, setSaving] = useState(false)
  const [action, setAction] = useState<ReauthAction | null>(null)

  useEffect(() => {
    const pendingAction = getPendingSettingsAction()
    if (pendingAction?.kind === 'unlinkPlatform') queueMicrotask(() => setAction(pendingAction))
  }, [])

  async function save(event: FormEvent<HTMLFormElement>, endpoint: ProfileEndpoint) {
    event.preventDefault()
    setSaving(true)
    setFormError(undefined)
    const values = new FormData(event.currentTarget)
    try {
      if (endpoint === '/api/v1/me/username') {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { username: String(values.get('username') ?? '') } })
      } else if (endpoint === '/api/v1/me/display-name') {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { displayName: String(values.get('displayName') ?? '') } })
      } else if (endpoint === '/api/v1/me/avatar') {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { kind: 'emoji', emoji: String(values.get('emoji') ?? ''), platformLinkId: null } })
      } else {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { timeZone: String(values.get('timeZone') ?? '') } })
      }
      await queryClient.invalidateQueries({ queryKey: profileQueryKey })
    } catch (caught) {
      setFormError(caught)
    } finally {
      setSaving(false)
    }
  }

  async function unlinkPlatform(id: string, proof?: { currentPassword?: string; reauthToken?: string }) {
    setFormError(undefined)
    try {
      await client.DELETE('/api/v1/me/platform-links/{id}', {
        params: { header: { 'X-CSRF-TOKEN': '' }, path: { id } },
        body: proof ? { currentPassword: proof.currentPassword ?? null, reauthToken: proof.reauthToken ?? null } : undefined,
      } as never)
      clearPendingSettingsAction()
      await queryClient.invalidateQueries({ queryKey: profileQueryKey })
    } catch (caught) {
      if (isApiError(caught) && caught.code === 'reauth_required' && !proof) {
        setAction({ kind: 'unlinkPlatform', id })
        return
      }
      setFormError(caught)
      throw caught
    }
  }

  async function confirmAction(proof: { currentPassword?: string; reauthToken?: string }) {
    if (action?.kind === 'unlinkPlatform') await unlinkPlatform(action.id, proof)
  }
  if (isPending) return <main className="mx-auto max-w-3xl space-y-6 p-4 sm:p-8" data-testid="screen-settings-profile"><SettingsNavigation /><p className="text-ink-soft">{t('loading')}</p></main>

  return (
    <main className="mx-auto max-w-3xl space-y-6 p-4 sm:p-8" data-testid="screen-settings-profile">
      <SettingsNavigation />
      <h1 className="font-display text-display font-bold text-ink">{t('pages.profile')}</h1>
      <ProfileError error={error} />
      <form className="space-y-2" onSubmit={(event) => void save(event, '/api/v1/me/username')}>
        <Label htmlFor="profile-username">{t('fields.username')}</Label>
        <Input id="profile-username" name="username" defaultValue={profile?.username ?? ''} required />
        <p className="text-meta text-ink-soft">{t('usernameCooldown')}</p>
        <Button disabled={saving} type="submit" variant="primary">{t('actions.saveUsername')}</Button>
      </form>
      <form className="space-y-2" onSubmit={(event) => void save(event, '/api/v1/me/display-name')}>
        <Label htmlFor="profile-display-name">{t('fields.displayName')}</Label>
        <Input id="profile-display-name" name="displayName" defaultValue={profile?.displayName ?? ''} required />
        <Button disabled={saving} type="submit" variant="neutral">{t('actions.saveDisplayName')}</Button>
      </form>
      <form className="space-y-2" onSubmit={(event) => void save(event, '/api/v1/me/avatar')}>
        <Label htmlFor="profile-avatar">{t('fields.avatar')}</Label>
        <Input id="profile-avatar" name="emoji" defaultValue={profile?.avatar?.emoji ?? 'game'} required />
        <Button disabled={saving} type="submit" variant="neutral">{t('actions.saveAvatar')}</Button>
      </form>
      <form className="space-y-2" onSubmit={(event) => void save(event, '/api/v1/me/time-zone')}>
        <Label htmlFor="profile-time-zone">{t('fields.timeZone')}</Label>
        <Input id="profile-time-zone" name="timeZone" defaultValue={profile?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone} required />
        <Button disabled={saving} type="submit" variant="neutral">{t('actions.saveTimeZone')}</Button>
      </form>
      <section aria-labelledby="platform-links-heading" className="space-y-3">
        <h2 className="font-display text-h2 font-semibold text-ink" id="platform-links-heading">{t('platformLinks.title')}</h2>
        {profile?.platformLinks.length ? <ul className="space-y-3">{profile.platformLinks.map((link) => <li className="rounded-card border-2 border-ink bg-card p-4 shadow-offset-card" key={link.id}>
          <span className="text-ink">{t('platformLinks.item', { platform: link.platform, handle: link.handle })}</span>
          {link.platform.toLowerCase() === 'steam' && <p className="mt-1 text-meta text-ink-soft">{t('platformLinks.steamUnlinkNotice')}</p>}
          <Button className="mt-2" variant="danger" onClick={() => void unlinkPlatform(link.id)}>{t('platformLinks.unlink', { platform: link.platform })}</Button>
        </li>)}</ul> : <p className="text-ink-soft">{t('platformLinks.empty')}</p>}
      </section>
      <ProfileError error={formError} />
      <ReauthPrompt action={action} methods={methods} onOpenChange={(open) => { if (!open) { clearPendingSettingsAction(); setAction(null) } }} onConfirmed={confirmAction} />
    </main>
  )
}