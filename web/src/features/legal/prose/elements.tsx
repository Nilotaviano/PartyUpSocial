import { useId, type ReactNode } from 'react'

/**
 * The legal documents are prose components, not markdown rendered at runtime
 * (PHASE8_DESIGN.md §3 S5: no markdown runtime, no new npm dependency per the
 * A-013 posture). These three elements carry the whole typographic vocabulary
 * the nine documents use, so a document body stays readable as prose.
 */
export function Section({
  title,
  children,
}: {
  title: string
  children: ReactNode
}) {
  const headingId = useId()

  return (
    <section aria-labelledby={headingId} className="space-y-3">
      <h2
        className="text-h2 font-display font-semibold text-ink"
        id={headingId}
      >
        {title}
      </h2>
      {children}
    </section>
  )
}

export function P({ children }: { children: ReactNode }) {
  return <p className="text-ink-soft">{children}</p>
}

export function Bullets({ items }: { items: ReactNode[] }) {
  return (
    <ul className="list-disc space-y-2 pl-6 text-ink-soft">
      {items.map((item, index) => (
        // The list is a fixed, authored sequence per document — it is never
        // reordered or filtered, so the index is a stable key.
        <li key={index}>{item}</li>
      ))}
    </ul>
  )
}
