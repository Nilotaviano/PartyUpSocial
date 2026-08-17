import { UsersRound } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Tag } from './GroupBits'

/**
 * Ported from the Lovable reference (`src/components/groups/ShareTag.tsx`),
 * minus its "Friends"/"Public" variants — those aren't in the spec (D-206
 * only defines the group-provenance presentation). Two arms:
 *  - named ("via D&D crew") when `viaGroup` is non-null — the viewer is that
 *    group's owner or an Accepted member, and the API only ever populates it
 *    from the live group row for such a viewer.
 *  - neutral ("Shared with a group") when `sharedWithGroup` is true but
 *    `viaGroup` is null — some other targeted group grants access, but not
 *    one the viewer belongs to; the group's name is never leaked here.
 * Renders nothing for a Public/Private target. Icon + text always together —
 * never colour alone.
 */
export function ShareTag({
  share,
}: {
  share: { viaGroup: { id: string; name: string } | null; sharedWithGroup: boolean } | undefined
}) {
  const { t } = useTranslation('groups')

  if (!share) return null

  if (share.viaGroup) {
    return (
      <Tag icon={UsersRound} tone="accent">
        {t('shareTag.via', { name: share.viaGroup.name })}
      </Tag>
    )
  }

  if (share.sharedWithGroup) {
    return <Tag icon={UsersRound}>{t('shareTag.sharedWithGroup')}</Tag>
  }

  return null
}
