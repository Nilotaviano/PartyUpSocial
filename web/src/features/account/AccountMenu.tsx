import * as DropdownMenuPrimitive from '@radix-ui/react-dropdown-menu'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { useLogoutMutation } from '../../auth/useSession'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'

const itemClasses =
  'flex min-h-11 cursor-pointer items-center rounded-[0.375rem] px-3 py-2 font-body text-body text-ink outline-none data-[highlighted]:bg-accent data-[highlighted]:text-accent-ink data-[disabled]:pointer-events-none data-[disabled]:opacity-50'

/**
 * D-183: the app chrome's trailing account menu. The trigger doubles as the D-182 tour's eighth
 * anchor (`data-tour="settings"`) — it must exist in the DOM regardless of the profile query's
 * state, so the tour anchor never disappears and the chrome never shifts while the profile loads.
 */
export function AccountMenu() {
  const { t } = useTranslation('common')
  const navigate = useNavigate()
  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const logout = useLogoutMutation()

  function signOut() {
    if (logout.isPending) return
    logout.mutate(undefined, {
      onSuccess: () => navigate('/welcome', { replace: true }),
    })
  }

  return (
    <DropdownMenuPrimitive.Root>
      <DropdownMenuPrimitive.Trigger asChild>
        <button
          aria-label={t('accountMenu.trigger')}
          className="inline-flex min-h-11 min-w-11 flex-none items-center justify-center rounded-control p-1 font-display focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          data-tour="settings"
          type="button"
        >
          <AvatarDisc
            emoji={profile.data?.avatar?.emoji}
            imgUrl={avatarImgUrl(profile.data?.avatar)}
            seed={profile.data?.userId ?? ''}
            size="xs"
          />
        </button>
      </DropdownMenuPrimitive.Trigger>
      <DropdownMenuPrimitive.Portal>
        <DropdownMenuPrimitive.Content
          align="end"
          className="min-w-[160px] overflow-hidden rounded-control border-2 border-ink bg-card p-1 font-body text-ink shadow-offset-card"
          sideOffset={8}
        >
          <DropdownMenuPrimitive.Item asChild className={itemClasses}>
            <Link to="/settings/account">{t('navigation.settings')}</Link>
          </DropdownMenuPrimitive.Item>
          <DropdownMenuPrimitive.Item
            className={itemClasses}
            disabled={logout.isPending}
            onSelect={signOut}
          >
            {t('accountMenu.signOut')}
          </DropdownMenuPrimitive.Item>
        </DropdownMenuPrimitive.Content>
      </DropdownMenuPrimitive.Portal>
    </DropdownMenuPrimitive.Root>
  )
}
