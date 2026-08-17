import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import '../../i18n'
import { ShareTag } from './ShareTag'

afterEach(() => {
  cleanup()
})

describe('ShareTag', () => {
  it('renders nothing when share is undefined', () => {
    const { container } = render(<ShareTag share={undefined} />)
    expect(container).toBeEmptyDOMElement()
  })

  it('renders nothing for a target with neither viaGroup nor sharedWithGroup', () => {
    const { container } = render(<ShareTag share={{ viaGroup: null, sharedWithGroup: false }} />)
    expect(container).toBeEmptyDOMElement()
  })

  it('masks the group name to a neutral tag when viaGroup is null but sharedWithGroup is true_D206', () => {
    render(<ShareTag share={{ viaGroup: null, sharedWithGroup: true }} />)
    expect(screen.getByText('Shared with a group')).toBeInTheDocument()
  })

  it('names the group when viaGroup is populated_D206', () => {
    render(<ShareTag share={{ viaGroup: { id: 'g1', name: 'D&D crew' }, sharedWithGroup: true }} />)
    expect(screen.getByText('via D&D crew')).toBeInTheDocument()
    // The neutral variant never renders alongside the named one.
    expect(screen.queryByText('Shared with a group')).not.toBeInTheDocument()
  })
})
