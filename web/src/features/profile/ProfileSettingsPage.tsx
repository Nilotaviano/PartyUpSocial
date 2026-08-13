import { useEffect, useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { ReauthPrompt, type ReauthAction } from '../settings/ReauthPrompt'
import { clearPendingSettingsAction, getPendingSettingsAction } from '../settings/reauthAction'
import { SettingsNavigation } from '../settings/BlockedUsersPage'
import { Input } from '../../components/ui/input'
import { FormField } from '../../components/ui/formShell'

import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { TimeZoneField, isKnownTimeZone } from '../../components/TimeZoneField'
import { AVATAR_CATALOG } from './avatarCatalog'
import { AvatarPicker } from './AvatarPicker'
import {
  avatarDraftBody,
  avatarSourcesOf,
  decodeAvatarSelection,
  selectionFromAvatar,
} from './avatarSources'

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

function AvatarField({
  defaultSelection,
  sources,
}: {
  defaultSelection: string
  sources: ReturnType<typeof avatarSourcesOf>
}) {
  const [selection, setSelection] = useState(defaultSelection)
  return (
    <>
      <AvatarPicker
        idPrefix="profile-avatar"
        labelledBy="profile-avatar-label"
        sources={sources}
        value={selection}
        onChange={setSelection}
      />
      <input name="avatarSelection" type="hidden" value={selection} />
    </>
  )
}

export function ProfileSettingsPage() {
  const { t } = useTranslation('profile')
  const queryClient = useQueryClient()
  const { data: profile, isPending, error } = useQuery({ queryKey: profileQueryKey, queryFn: getProfile })
  const { data: methods = [] } = useQuery({ queryKey: methodsQueryKey, queryFn: getAuthMethods })
  const [formError, setFormError] = useState<unknown>()
  const [saving, setSaving] = useState(false)
  const [action, setAction] = useState<ReauthAction | null>(null)
  const [timeZoneInvalid, setTimeZoneInvalid] = useState(false)

  useEffect(() => {
    const pendingAction = getPendingSettingsAction()
    if (pendingAction?.kind === 'unlinkPlatform') queueMicrotask(() => setAction(pendingAction))
  }, [])

  async function save(event: FormEvent<HTMLFormElement>, endpoint: ProfileEndpoint) {
    event.preventDefault()
    setFormError(undefined)
    const values = new FormData(event.currentTarget)
    if (endpoint === '/api/v1/me/time-zone') {
      const timeZone = String(values.get('timeZone') ?? '')
      if (!isKnownTimeZone(timeZone, profile?.timeZone)) {
        setTimeZoneInvalid(true)
        return
      }
      setTimeZoneInvalid(false)
    }
    setSaving(true)
    try {
      if (endpoint === '/api/v1/me/username') {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { username: String(values.get('username') ?? '') } })
      } else if (endpoint === '/api/v1/me/display-name') {
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { displayName: String(values.get('displayName') ?? '') } })
      } else if (endpoint === '/api/v1/me/avatar') {
        const selection = decodeAvatarSelection(String(values.get('avatarSelection') ?? ''))
        await client.PUT(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: avatarDraftBody(selection) })
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
  if (isPending)
    return (
      <main data-testid="screen-settings-profile">
        <PageScaffold>
          <PageHeader title={t('pages.profile')} />
          <SettingsNavigation />
          <p className="text-ink-soft">{t('loading')}</p>
        </PageScaffold>
      </main>
    )

  return (
    <main data-testid="screen-settings-profile">
      <PageScaffold>
        <PageHeader title={t('pages.profile')} />
        <SettingsNavigation />
        <ProfileError error={error} />
        <form
          className="flex max-w-form flex-col gap-4"
          onSubmit={(event) => void save(event, '/api/v1/me/username')}
        >
          <FormField
            hint={t('usernameCooldown')}
            htmlFor="profile-username"
            label={t('fields.username')}
          >
            <Input id="profile-username" name="username" defaultValue={profile?.username ?? ''} required />
          </FormField>
          <Button className="self-start" disabled={saving} type="submit" variant="primary">{t('actions.saveUsername')}</Button>
        </form>
        <form
          className="flex max-w-form flex-col gap-4"
          onSubmit={(event) => void save(event, '/api/v1/me/display-name')}
        >
          <FormField htmlFor="profile-display-name" label={t('fields.displayName')}>
            <Input id="profile-display-name" name="displayName" defaultValue={profile?.displayName ?? ''} required />
          </FormField>
          <Button className="self-start" disabled={saving} type="submit" variant="neutral">{t('actions.saveDisplayName')}</Button>
        </form>
        <form
          className="flex max-w-form flex-col gap-4"
          onSubmit={(event) => void save(event, '/api/v1/me/avatar')}
        >
          {/* Not a `FormField`: the picker is a radiogroup labelled by this
              node, and a `<label>` with no form control to point at would be a
              broken association rather than a nicer one. */}
          <p className="font-display text-sm font-bold text-ink" id="profile-avatar-label">{t('fields.avatar')}</p>
          <AvatarField
            defaultSelection={selectionFromAvatar(profile?.avatar, AVATAR_CATALOG[0].glyph)}
            sources={avatarSourcesOf(profile)}
          />
          <Button className="self-start" disabled={saving} type="submit" variant="neutral">{t('actions.saveAvatar')}</Button>
        </form>
        <form
          className="flex max-w-form flex-col gap-4"
          noValidate
          onSubmit={(event) => void save(event, '/api/v1/me/time-zone')}
        >
          <TimeZoneField
            defaultValue={profile?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone}
            errorMessage={timeZoneInvalid ? t('timeZoneField.invalid') : undefined}
            hint={t('timeZoneField.hint')}
            id="profile-time-zone"
            label={t('fields.timeZone')}
            name="timeZone"
            storedZone={profile?.timeZone}
          />
          <Button className="self-start" disabled={saving} type="submit" variant="neutral">{t('actions.saveTimeZone')}</Button>
        </form>
        <Section title={t('platformLinks.title')} titleId="platform-links-heading">
          {profile?.platformLinks.length ? (
            <ul>
              {profile.platformLinks.map((link) => (
                <li className="border-t border-ink/25 first:border-t-0" key={link.id}>
                  <div className="flex flex-col gap-2 px-2 py-3">
                    <span className="text-ink">{t('platformLinks.item', { platform: link.platform, handle: link.handle })}</span>
                    {link.platform.toLowerCase() === 'steam' && <p className="text-meta text-ink-soft">{t('platformLinks.steamUnlinkNotice')}</p>}
                    <Button className="self-start" variant="danger" onClick={() => void unlinkPlatform(link.id)}>{t('platformLinks.unlink', { platform: link.platform })}</Button>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-ink-soft">{t('platformLinks.empty')}</p>
          )}
        </Section>
        <ProfileError error={formError} />
        <ReauthPrompt action={action} methods={methods} onOpenChange={(open) => { if (!open) { clearPendingSettingsAction(); setAction(null) } }} onConfirmed={confirmAction} />
      </PageScaffold>
    </main>
  )
}