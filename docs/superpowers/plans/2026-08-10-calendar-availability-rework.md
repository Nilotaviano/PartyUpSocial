# Calendar / Availability Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make PartyUp's availability surface satisfy the decisions it already claims — create and delete recurring availability, adjust existing windows by pointer, and read the week without a 24-hour scroll.

**Architecture:** Keep the custom `WeekGrid` (FullCalendar was evaluated and rejected — see `CALENDAR_DESIGN.md` §2). One backend addition carries series definitions onto the existing availability layer; the rest is client work in `web/src/features/availability/`, composed as small focused components rather than growing `WeekGridPage` further.

**Tech Stack:** .NET 10 / EF Core / NodaTime backend; React 19 + TanStack Query + Radix + Tailwind (v4 `@utility` tokens) frontend; xUnit + Testcontainers; Vitest + Testing Library.

**Spec source:** `CALENDAR_DESIGN.md` (owner-approved 2026-08-10).

## Global Constraints

- **No new D-numbers.** Everything implements Confirmed decisions: D-009, D-025, D-111, D-117, §7. Do not add or amend spec rows.
- **All user-facing strings go through i18next in `en`, `pt-BR`, `es`** in the same commit. No English left inside a translated string.
- **WCAG 2.2 AA is scope, not polish.** Every new control is keyboard reachable, has a visible label or an accessible name, and a ≥44px touch target (`min-h-11`).
- **`dotnet build PartyUp.sln` must stay at 0 warnings** (TreatWarningsAsErrors).
- **No new dependencies.** A-013.
- **No changelog comments in code.** Rationale goes in the commit message. A comment explains a non-obvious current-state constraint only.
- **Semantic Tailwind tokens only** (`bg-card`, `border-ink`, `text-ink`, `bg-accent`, `text-accent-ink`, `bg-free`, `shadow-offset-card`). No raw hex, no new colors.
- **Docker lives in WSL, not the Windows host.** Integration tests run via:
  `wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests --filter '<Filter>' -p:ArtifactsPath=/tmp/partyup-artifacts"`
  `/tmp` inside WSL does not survive an idle-out — build in the same invocation.
- **Do not run full test suites** unless the owner asks. Targeted filters only.
- **`npm run gen:api` requires the API running concurrently** (`dotnet run --project src/PartyUp.Api`). Never hand-edit `web/src/api/schema.d.ts`.
- **The `touch-action` gating in `WeekGrid` is load-bearing** (P5-TOUCH-01, commit `e1a3dd2`): before a long-press fires, a finger-drag must still scroll the page. No task may break this.

## File Structure

| File | Responsibility |
|---|---|
| `src/PartyUp.Application/Availability/AvailabilityReadModels.cs` | `MyAvailabilityLayer` gains `Series` |
| `src/PartyUp.Application/Availability/ReadMyAvailabilityUseCase.cs` | passes the already-fetched `data.Series` onto the layer |
| `src/PartyUp.Api/Contracts/Availability/AvailabilityContracts.cs` | `MyAvailabilityLayerResponse` gains `Series` |
| `web/src/features/availability/availabilityQueries.ts` | series-aware types; existing helpers unchanged |
| `web/src/features/availability/AvailabilitySummary.tsx` | **new** — rules list, series + one-off delete |
| `web/src/features/availability/CommitBlockDialog.tsx` | **new** — range confirm + one-time/weekly choice |
| `web/src/features/availability/WeekGrid.tsx` | viewport clamp, sticky header/gutter, resize/move handles |
| `web/src/features/availability/WeekGridPage.tsx` | wires the two new components; routes drags to the scope dialog |
| `web/src/i18n/{en,pt-BR,es}/availability.json` | all new copy |

Tasks are ordered so each leaves the app working: the backend field lands first (unused), then each client surface consumes it.

---

### Task 1: Carry series definitions onto the availability layer

**Files:**
- Modify: `src/PartyUp.Application/Availability/AvailabilityReadModels.cs:44-47`
- Modify: `src/PartyUp.Application/Availability/ReadMyAvailabilityUseCase.cs:31,62`
- Modify: `src/PartyUp.Api/Contracts/Availability/AvailabilityContracts.cs:191-201`
- Test: `tests/PartyUp.Api.IntegrationTests/Availability/` (existing availability test file — locate the one covering `GET /calendar` or `GET /me/availability` and extend it)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `MyAvailabilityLayerResponse.Series` — a `IReadOnlyList<AvailabilitySeriesView>` present on **both** `GET /api/v1/me/availability` and `GET /api/v1/calendar` (`GetCalendarViewUseCase` calls `ReadMyAvailabilityUseCase`, so one change serves both). `AvailabilitySeriesView` already exists with `SeriesId`, `DayOfWeek`, `StartTime`, `DurationMinutes`, `ZoneId`, `EffectiveFrom`, `EffectiveUntil`. Client-side this surfaces as `calendar.data.myAvailability.series`.

- [ ] **Step 1: Write the failing integration test**

Locate the existing integration test that asserts the `GET /api/v1/calendar` response shape and add:

```csharp
[Fact]
public async Task Calendar_ReturnsTheViewersSeriesDefinitions_D025()
{
    // Arrange: an onboarded viewer with one weekly series whose only occurrence
    // this week is irrelevant to the assertion — the definition must come back
    // regardless of expansion, which is the whole point of the field.
    await CreateWeeklySeriesAsync(dayOfWeek: 2, startTime: "20:00", durationMinutes: 180);

    JsonElement body = await GetCalendarAsync(from: WeekStart, to: WeekEnd);

    JsonElement series = body.GetProperty("myAvailability").GetProperty("series");
    Assert.Equal(1, series.GetArrayLength());
    JsonElement only = series[0];
    Assert.Equal(2, only.GetProperty("dayOfWeek").GetInt32());
    Assert.Equal("20:00", only.GetProperty("startTime").GetString());
    Assert.Equal(180, only.GetProperty("durationMinutes").GetInt32());
    Assert.False(string.IsNullOrWhiteSpace(only.GetProperty("zoneId").GetString()));
}
```

Match the file's existing helper names and fixture idiom — do not invent a new harness. If the file has no `CreateWeeklySeriesAsync`/`GetCalendarAsync` equivalent, write the arrange/act inline using the same client the neighbouring tests use.

- [ ] **Step 2: Run it and verify it fails**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests --filter 'FullyQualifiedName~Calendar_ReturnsTheViewersSeriesDefinitions_D025' -p:ArtifactsPath=/tmp/partyup-artifacts"
```
Expected: FAIL — `myAvailability` has no `series` property (`KeyNotFoundException` from `GetProperty`).

- [ ] **Step 3: Add `Series` to the application read model**

`AvailabilityReadModels.cs`:

```csharp
public sealed record MyAvailabilityLayer(
    IReadOnlyList<MyAvailabilityOccurrence> Occurrences,
    IReadOnlyList<TimeRange> Merged,
    TimeRange Range,
    IReadOnlyList<AvailabilitySeries> Series);
```

Use whatever type `AvailabilityOwnerData.Series` already holds — do not introduce a parallel type.

- [ ] **Step 4: Populate it in the use case**

`ReadMyAvailabilityUseCase.cs` — the empty-owner early return at :31 and the main return at :62:

```csharp
// :31
return new MyAvailabilityLayer([], [], range, []);

// :62
return new MyAvailabilityLayer(occurrences, merged, range, data.Series);
```

`data.Series` is already in scope — it is passed to `AvailabilityProjection.Project` at :60. No new query.

- [ ] **Step 5: Surface it on the wire contract**

`AvailabilityContracts.cs`:

```csharp
public sealed record MyAvailabilityLayerResponse(
    IReadOnlyList<MyAvailabilityOccurrenceView> Occurrences,
    IReadOnlyList<TimeRangeView> Merged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeRangeView? Range,
    IReadOnlyList<AvailabilitySeriesView> Series)
{
    public static MyAvailabilityLayerResponse From(MyAvailabilityLayer layer, bool includeRange) =>
        new(
            [.. layer.Occurrences.Select(MyAvailabilityOccurrenceView.From)],
            [.. layer.Merged.Select(TimeRangeView.From)],
            includeRange ? TimeRangeView.From(layer.Range) : null,
            [.. layer.Series.Select(AvailabilitySeriesView.From)]);
}
```

`Series` is **not** `WhenWritingNull` — an empty list must serialize as `[]` so the client never has to distinguish absent from empty. Confirm `AvailabilitySeriesView.From` exists; if its parameter type differs from `layer.Series`'s element type, map explicitly rather than changing either type.

- [ ] **Step 6: Run the test and the availability suite**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet build PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts && dotnet test tests/PartyUp.Api.IntegrationTests --filter 'FullyQualifiedName~Availability|FullyQualifiedName~Calendar' -p:ArtifactsPath=/tmp/partyup-artifacts"
```
Expected: PASS, 0 warnings. `MyAvailabilityLayerResponse` carries `[JsonUnmappedMemberHandling(Disallow)]`, which governs *deserialization* — adding a written property is safe, but if any test round-trips this response into the record, it will now require the field; fix such a test by supplying it, never by loosening the attribute.

- [ ] **Step 7: Regenerate the client contract**

In one terminal: `dotnet run --project src/PartyUp.Api`. In another, from `web/`: `npm run gen:api`.
Verify `web/src/api/schema.d.ts` now shows `series` on `MyAvailabilityLayerResponse`. Do not hand-edit.

- [ ] **Step 8: Commit**

```bash
git add src/PartyUp.Application/Availability src/PartyUp.Api/Contracts/Availability tests/PartyUp.Api.IntegrationTests web/src/api/schema.d.ts
git commit -m "feat(availability): return series definitions on the availability layer

The summary panel needs the viewer's recurring rules, but both GET
/me/availability and GET /calendar returned only expanded occurrences carrying
origin.seriesId. Grouping those would silently omit a series whose
effectiveFrom is in the future or whose only occurrence that week is skipped.

MyAvailabilityLayer gains Series, surfaced on MyAvailabilityLayerResponse. One
addition serves both endpoints because GetCalendarViewUseCase calls
ReadMyAvailabilityUseCase rather than re-implementing it, and the data was
already in hand -- ReadMyAvailabilityUseCase passes data.Series to the
projection today, so no new query is added. No new route, so the op-id and
antiforgery inventories are unchanged."
```

---

### Task 2: Viewport — hour clamp, sticky header and gutter, initial scroll

**Files:**
- Modify: `web/src/features/availability/WeekGrid.tsx` (`startHour` default at :108, the container at :430, cell/header markup)
- Modify: `web/src/features/availability/WeekGridPage.tsx` (passes the range, owns the expand toggle)
- Modify: `web/src/i18n/{en,pt-BR,es}/availability.json`
- Test: `web/src/features/availability/WeekGridPage.test.tsx`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `WeekGrid` accepts the existing `startHour`/`endHour` props (unchanged signature). `WeekGridPage` owns `const [showEarlyHours, setShowEarlyHours] = useState(false)` and passes `startHour={showEarlyHours ? 0 : 7}`.

- [ ] **Step 1: Write the failing tests**

Add to `WeekGridPage.test.tsx`:

```tsx
it('renders the day from 07:00 by default instead of midnight', async () => {
  renderPage()

  expect(await screen.findByText('07:00')).toBeInTheDocument()
  expect(screen.queryByText('00:00')).not.toBeInTheDocument()
})

it('reveals the early hours on request', async () => {
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: 'Show earlier hours' }))

  expect(await screen.findByText('00:00')).toBeInTheDocument()
})
```

Match the file's existing `renderPage` helper and its `api.get` mock shape. If time labels render in a different format than `07:00`, assert the format the component actually produces — read `timeLabelFormatter` usage first rather than guessing.

- [ ] **Step 2: Run and verify they fail**

Run: `cd web && npx vitest run src/features/availability/WeekGridPage.test.tsx -t "07:00"`
Expected: FAIL — `00:00` is present (the grid starts at midnight) and no "Show earlier hours" control exists.

- [ ] **Step 3: Add the clamp and the toggle**

In `WeekGridPage.tsx`, above the grid:

```tsx
const [showEarlyHours, setShowEarlyHours] = useState(false)
```

Pass to the grid:

```tsx
<WeekGrid
  endHour={24}
  startHour={showEarlyHours ? 0 : 7}
  /* existing props unchanged */
/>
```

Render the toggle immediately above the grid:

```tsx
{!showEarlyHours && (
  <Button type="button" variant="neutral" onClick={() => setShowEarlyHours(true)}>
    {t('viewport.showEarlyHours')}
  </Button>
)}
```

- [ ] **Step 4: Add the i18n keys**

`web/src/i18n/en/availability.json` — add under a new `viewport` block:

```json
"viewport": {
  "showEarlyHours": "Show earlier hours"
}
```

`pt-BR`: `"showEarlyHours": "Mostrar horas anteriores"`
`es`: `"showEarlyHours": "Mostrar horas anteriores"`

- [ ] **Step 5: Make the header row and time gutter sticky**

In `WeekGrid.tsx`, add `sticky top-0 z-20 bg-card` to the day-header row and `sticky left-0 z-10 bg-card` to the time-label cells. The scroll container needs `relative` positioning for sticky to resolve against it.

The header must keep its existing `role`/`scope` semantics — sticky is a positioning change only. If the grid uses `role="grid"` with `role="columnheader"` cells, those attributes stay exactly as they are.

- [ ] **Step 6: Scroll to the first meaningful hour on mount**

In `WeekGridPage.tsx`, after the calendar query resolves, scroll the grid container so the earliest of (the viewer's earliest occurrence start that week, else 18:00) is at the top:

```tsx
const gridScroller = useRef<HTMLDivElement>(null)
const didInitialScroll = useRef(false)

useEffect(() => {
  if (didInitialScroll.current) return
  const occurrences = calendar.data?.myAvailability.occurrences ?? []
  if (!calendar.data) return
  didInitialScroll.current = true

  const earliestHour = occurrences.length
    ? Math.min(
        ...occurrences.map((occurrence) =>
          instantToFakeLocal(occurrence.startTime, timeZone).getUTCHours(),
        ),
      )
    : 18
  const target = Math.max(earliestHour, showEarlyHours ? 0 : 7)
  const row = gridScroller.current?.querySelector<HTMLElement>(
    `[data-hour="${target}"]`,
  )
  row?.scrollIntoView({ block: 'start', behavior: 'auto' })
}, [calendar.data, showEarlyHours, timeZone])
```

This requires `WeekGrid` to stamp `data-hour={hour}` on the first cell of each hour row. Add that attribute; it is product markup in the same spirit as `data-tour`, not a test hook.

- [ ] **Step 7: Run the tests**

Run: `cd web && npx vitest run src/features/availability/ && npm run lint`
Expected: PASS, lint clean. If an existing test asserted a midnight-anchored grid, update it — and say so in the commit body rather than silently.

- [ ] **Step 8: Commit**

```bash
git add web/src/features/availability web/src/i18n
git commit -m "feat(availability): clamp the grid to waking hours with sticky headers

The grid rendered startHour 0 to endHour 24 at 15-minute slots -- 96 rows --
and WeekGridPage overrode neither, so opening the calendar meant a full-day
scroll starting at midnight. It now defaults to 07:00-24:00 (68 rows) with an
explicit control to reveal the early hours.

The day header sticks on vertical scroll and the time gutter sticks on
horizontal scroll; the gutter matters most on a phone, where scrolling sideways
previously lost the hour labels entirely. Sticky is a positioning change only --
the grid's ARIA row/column semantics are untouched.

Initial scroll lands on the viewer's earliest window that week, else 18:00."
```

---

### Task 3: Availability summary panel with series deletion (closes D-025)

**Files:**
- Create: `web/src/features/availability/AvailabilitySummary.tsx`
- Create: `web/src/features/availability/AvailabilitySummary.test.tsx`
- Modify: `web/src/features/availability/WeekGridPage.tsx` (render the panel, wire delete mutations)
- Modify: `web/src/i18n/{en,pt-BR,es}/availability.json`

**Interfaces:**
- Consumes: `calendar.data.myAvailability.series` from Task 1 — `AvailabilitySeriesView[]` with `seriesId`, `dayOfWeek` (0=Sunday), `startTime` (`"HH:mm"`), `durationMinutes`, `zoneId`, `effectiveFrom`, `effectiveUntil`. Also `calendar.data.myAvailability.occurrences` for one-off blocks (`origin.kind === 'block'` carries `origin.blockId`).
- Produces: 

```tsx
export type AvailabilitySummaryProps = {
  series: AvailabilitySeriesView[]
  oneOffs: { blockId: string; startTime: string; endTime: string }[]
  timeZone: string
  onDeleteSeries: (seriesId: string) => void
  onDeleteBlock: (blockId: string) => void
  deletingId: string | null
}
export function AvailabilitySummary(props: AvailabilitySummaryProps): JSX.Element
```

- [ ] **Step 1: Write the failing test**

Create `AvailabilitySummary.test.tsx`:

```tsx
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import '../../i18n'
import { AvailabilitySummary } from './AvailabilitySummary'

afterEach(cleanup)

const series = [
  {
    seriesId: 'series-1',
    dayOfWeek: 2,
    startTime: '20:00',
    durationMinutes: 180,
    zoneId: 'America/Sao_Paulo',
    effectiveFrom: '2026-08-01',
    effectiveUntil: null,
  },
]

describe('AvailabilitySummary', () => {
  it('renders a weekly series as a readable rule_D025', () => {
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={series}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={vi.fn()}
      />,
    )

    expect(screen.getByText(/Tue/)).toBeInTheDocument()
    expect(screen.getByText(/20:00/)).toBeInTheDocument()
    expect(screen.getByText(/23:00/)).toBeInTheDocument()
  })

  it('deletes the whole series after confirming_D025', () => {
    const onDeleteSeries = vi.fn()
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={series}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={onDeleteSeries}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /Delete/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Delete series' }))

    expect(onDeleteSeries).toHaveBeenCalledWith('series-1')
  })

  it('shows a series that has no occurrence in the visible week_D025', () => {
    render(
      <AvailabilitySummary
        deletingId={null}
        oneOffs={[]}
        series={[{ ...series[0], effectiveFrom: '2099-01-01' }]}
        timeZone="America/Sao_Paulo"
        onDeleteBlock={vi.fn()}
        onDeleteSeries={vi.fn()}
      />,
    )

    expect(screen.getByText(/Tue/)).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Run and verify it fails**

Run: `cd web && npx vitest run src/features/availability/AvailabilitySummary.test.tsx`
Expected: FAIL — module `./AvailabilitySummary` does not exist.

- [ ] **Step 3: Implement the component**

```tsx
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import type { AvailabilitySeriesView } from './availabilityQueries'

export type AvailabilitySummaryProps = {
  series: AvailabilitySeriesView[]
  oneOffs: { blockId: string; startTime: string; endTime: string }[]
  timeZone: string
  onDeleteSeries: (seriesId: string) => void
  onDeleteBlock: (blockId: string) => void
  deletingId: string | null
}

function endTime(startTime: string, durationMinutes: number) {
  const [hours, minutes] = startTime.split(':').map(Number)
  const total = hours * 60 + minutes + durationMinutes
  const wrapped = total % (24 * 60)
  return `${String(Math.floor(wrapped / 60)).padStart(2, '0')}:${String(wrapped % 60).padStart(2, '0')}`
}

export function AvailabilitySummary({
  series,
  oneOffs,
  timeZone,
  onDeleteSeries,
  onDeleteBlock,
  deletingId,
}: AvailabilitySummaryProps) {
  const { t, i18n } = useTranslation('availability')
  const [pendingSeries, setPendingSeries] = useState<string | null>(null)

  const weekdayName = (dayOfWeek: number) => {
    // 2026-08-02 is a Sunday, so adding dayOfWeek lands on the right weekday.
    const date = new Date(Date.UTC(2026, 7, 2 + dayOfWeek))
    return new Intl.DateTimeFormat(i18n.language, {
      weekday: 'short',
      timeZone: 'UTC',
    }).format(date)
  }

  return (
    <section aria-labelledby="availability-summary-heading" className="space-y-3">
      <h2
        className="font-display text-h2 font-semibold text-ink"
        id="availability-summary-heading"
      >
        {t('summary.heading')}
      </h2>
      {series.length === 0 && oneOffs.length === 0 ? (
        <p className="text-ink-soft">{t('summary.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {series.map((rule) => (
            <li
              className="flex flex-wrap items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
              key={rule.seriesId}
            >
              <span className="text-ink">
                {t('summary.weeklyRule', {
                  weekday: weekdayName(Number(rule.dayOfWeek)),
                  start: rule.startTime,
                  end: endTime(rule.startTime, Number(rule.durationMinutes)),
                })}
              </span>
              <Button
                disabled={deletingId === rule.seriesId}
                type="button"
                variant="danger"
                onClick={() => setPendingSeries(rule.seriesId)}
              >
                {t('summary.delete')}
              </Button>
            </li>
          ))}
          {oneOffs.map((block) => (
            <li
              className="flex flex-wrap items-center justify-between gap-2 rounded-card border-2 border-ink bg-card p-3 shadow-offset-card"
              key={block.blockId}
            >
              <span className="text-ink">
                {new Intl.DateTimeFormat(i18n.language, {
                  weekday: 'short',
                  day: 'numeric',
                  month: 'short',
                  hour: '2-digit',
                  minute: '2-digit',
                  timeZone,
                }).format(new Date(block.startTime))}
              </span>
              <Button
                disabled={deletingId === block.blockId}
                type="button"
                variant="danger"
                onClick={() => onDeleteBlock(block.blockId)}
              >
                {t('summary.delete')}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <Dialog.Root
        open={pendingSeries !== null}
        onOpenChange={(open) => !open && setPendingSeries(null)}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 bg-scrim" />
          <Dialog.Content
            aria-describedby="delete-series-description"
            className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          >
            <Dialog.Title className="text-h2 font-display font-semibold text-ink">
              {t('summary.deleteSeriesTitle')}
            </Dialog.Title>
            <Dialog.Description className="mt-2 text-ink-soft" id="delete-series-description">
              {t('summary.deleteSeriesBody')}
            </Dialog.Description>
            <div className="mt-6 flex flex-wrap justify-end gap-2">
              <Dialog.Close asChild>
                <Button type="button" variant="neutral">
                  {t('summary.cancel')}
                </Button>
              </Dialog.Close>
              <Button
                type="button"
                variant="danger"
                onClick={() => {
                  const target = pendingSeries
                  setPendingSeries(null)
                  if (target) onDeleteSeries(target)
                }}
              >
                {t('summary.deleteSeriesConfirm')}
              </Button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </section>
  )
}
```

Check `Dialog`'s export shape in `web/src/components/ui/dialog.tsx` before using it and match the idiom in `SessionDetailPage.tsx`'s `CancelSessionDialog`, including its `onOpenAutoFocus` focus handling.

- [ ] **Step 4: Add the i18n keys**

`en`:
```json
"summary": {
  "heading": "Your availability",
  "empty": "You have not published any availability yet.",
  "weeklyRule": "Every {{weekday}} · {{start}}–{{end}}",
  "delete": "Delete",
  "cancel": "Keep it",
  "deleteSeriesTitle": "Delete this weekly availability?",
  "deleteSeriesBody": "This removes every future occurrence of this weekly window. Occurrences you already changed individually are removed too.",
  "deleteSeriesConfirm": "Delete series"
}
```

`pt-BR`:
```json
"summary": {
  "heading": "Sua disponibilidade",
  "empty": "Você ainda não publicou nenhuma disponibilidade.",
  "weeklyRule": "Toda {{weekday}} · {{start}}–{{end}}",
  "delete": "Excluir",
  "cancel": "Manter",
  "deleteSeriesTitle": "Excluir esta disponibilidade semanal?",
  "deleteSeriesBody": "Isto remove todas as ocorrências futuras desta janela semanal. As ocorrências que você já alterou individualmente também são removidas.",
  "deleteSeriesConfirm": "Excluir série"
}
```

`es`:
```json
"summary": {
  "heading": "Tu disponibilidad",
  "empty": "Todavía no has publicado ninguna disponibilidad.",
  "weeklyRule": "Cada {{weekday}} · {{start}}–{{end}}",
  "delete": "Eliminar",
  "cancel": "Conservar",
  "deleteSeriesTitle": "¿Eliminar esta disponibilidad semanal?",
  "deleteSeriesBody": "Esto elimina todas las ocurrencias futuras de esta ventana semanal. Las ocurrencias que ya cambiaste por separado también se eliminan.",
  "deleteSeriesConfirm": "Eliminar serie"
}
```

Verify the deleteSeriesBody claim against `Poll`-style domain behavior before shipping: if deleting a series does **not** remove individually-edited materialized occurrences, correct the copy in all three locales to match what the API actually does. Do not ship copy that overstates the effect.

- [ ] **Step 5: Wire it into the page**

In `WeekGridPage.tsx`, add the mutations and render the panel above the grid on mobile / beside it on desktop:

```tsx
const deleteSeries = useMutation({
  mutationFn: (seriesId: string) => deleteAvailabilitySeries(seriesId),
  onSuccess: () => invalidateAvailabilityQueries(queryClient),
})
```

```tsx
<AvailabilitySummary
  deletingId={
    deleteSeries.isPending ? (deleteSeries.variables ?? null) : null
  }
  oneOffs={oneOffBlocks}
  series={calendar.data?.myAvailability.series ?? []}
  timeZone={timeZone}
  onDeleteBlock={(blockId) => deleteBlock.mutate(blockId)}
  onDeleteSeries={(seriesId) => deleteSeries.mutate(seriesId)}
/>
```

Derive `oneOffBlocks` from `calendar.data.myAvailability.occurrences` where `origin.kind === 'block'`, mapping to `{ blockId, startTime, endTime }`.

- [ ] **Step 6: Run the tests**

Run: `cd web && npx vitest run src/features/availability/ && npm run lint && npm run build`
Expected: PASS, lint clean, build succeeds.

- [ ] **Step 7: Commit**

```bash
git add web/src/features/availability web/src/i18n
git commit -m "feat(availability): summary panel with series deletion (D-025)

D-025 says recurring availability supports removing one occurrence or the
entire series. Only the occurrence half shipped: deleteAvailabilitySeries
existed in availabilityQueries and the endpoint shipped, but nothing in the UI
called it, so a whole weekly window could never be removed.

The summary lists the viewer's own availability as readable rules rather than
grid marks, with series deletion behind a confirm that states it removes every
future occurrence. Per-occurrence deletion stays in the grid, where 'this one'
is the natural gesture.

It reads the series definitions added in the previous commit rather than
grouping the visible week's occurrences, so a series whose effectiveFrom is in
the future still appears."
```

---

### Task 4: Commit step with recurrence choice and undo (closes D-009 / §7)

**Files:**
- Create: `web/src/features/availability/CommitBlockDialog.tsx`
- Modify: `web/src/features/availability/WeekGridPage.tsx:366-371` (`handleCreateBlock`)
- Modify: `web/src/i18n/{en,pt-BR,es}/availability.json`
- Test: `web/src/features/availability/WeekGridPage.test.tsx`

**Interfaces:**
- Consumes: `GridRange` from `WeekGrid` (`{ start: Date; end: Date }` in fake-local convention), and the existing `createAvailabilityBlock` / `createAvailabilitySeries` helpers.
- Produces:

```tsx
export type CommitBlockDialogProps = {
  range: { start: Date; end: Date } | null
  timeZone: string
  pending: boolean
  onCancel: () => void
  onConfirm: (recurrence: 'once' | 'weekly') => void
}
export function CommitBlockDialog(props: CommitBlockDialogProps): JSX.Element
```

`CreateAvailabilitySeriesRequest` is `{ dayOfWeek, startTime, durationMinutes, effectiveFrom, effectiveUntil }` — `startTime` is `"HH:mm"`, `dayOfWeek` is 0=Sunday, `effectiveUntil` is `null` for an open-ended series.

- [ ] **Step 1: Write the failing tests**

Add to `WeekGridPage.test.tsx`:

```tsx
it('asks for a recurrence choice before saving a painted range_D009', async () => {
  renderPage()
  await paintRange()

  expect(await screen.findByRole('dialog')).toBeInTheDocument()
  expect(api.post).not.toHaveBeenCalled()
})

it('creates a weekly series when the range repeats_D009', async () => {
  renderPage()
  await paintRange()

  fireEvent.click(await screen.findByRole('radio', { name: 'Repeats weekly' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() =>
    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/me/availability/series',
      expect.objectContaining({
        body: expect.objectContaining({ durationMinutes: expect.any(Number) }),
      }),
    ),
  )
})

it('creates a one-time block by default_D009', async () => {
  renderPage()
  await paintRange()

  fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

  await waitFor(() =>
    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/me/availability/blocks',
      expect.anything(),
    ),
  )
})

it('discards the range when the commit step is dismissed_D009', async () => {
  renderPage()
  await paintRange()

  fireEvent.click(await screen.findByRole('button', { name: 'Discard' }))

  expect(api.post).not.toHaveBeenCalled()
})
```

Write `paintRange()` as a local helper driving the same drag the existing create test uses — read that test first and reuse its exact event sequence rather than inventing one.

- [ ] **Step 2: Run and verify they fail**

Run: `cd web && npx vitest run src/features/availability/WeekGridPage.test.tsx -t "_D009"`
Expected: FAIL — no dialog appears; `api.post` is called immediately with the blocks route.

- [ ] **Step 3: Implement the dialog**

```tsx
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'

export type CommitBlockDialogProps = {
  range: { start: Date; end: Date } | null
  timeZone: string
  pending: boolean
  onCancel: () => void
  onConfirm: (recurrence: 'once' | 'weekly') => void
}

export function CommitBlockDialog({
  range,
  timeZone,
  pending,
  onCancel,
  onConfirm,
}: CommitBlockDialogProps) {
  const { t, i18n } = useTranslation('availability')
  const [recurrence, setRecurrence] = useState<'once' | 'weekly'>('once')

  if (!range) return null

  const label = new Intl.DateTimeFormat(i18n.language, {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: 'UTC',
  }).format(range.start)

  return (
    <Dialog.Root open onOpenChange={(open) => !open && onCancel()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="commit-block-description"
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('commit.title')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="commit-block-description">
            {label}
          </Dialog.Description>
          <fieldset className="mt-4">
            <legend className="font-body font-semibold text-ink">
              {t('commit.recurrenceLegend')}
            </legend>
            <label className="mt-1 inline-flex min-h-11 items-center gap-2">
              <input
                checked={recurrence === 'once'}
                name="recurrence"
                type="radio"
                onChange={() => setRecurrence('once')}
              />
              {t('commit.once')}
            </label>
            <label className="ml-4 inline-flex min-h-11 items-center gap-2">
              <input
                checked={recurrence === 'weekly'}
                name="recurrence"
                type="radio"
                onChange={() => setRecurrence('weekly')}
              />
              {t('commit.weekly')}
            </label>
          </fieldset>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Button disabled={pending} type="button" variant="neutral" onClick={onCancel}>
              {t('commit.discard')}
            </Button>
            <Button
              disabled={pending}
              type="button"
              variant="primary"
              onClick={() => onConfirm(recurrence)}
            >
              {t('commit.save')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
```

- [ ] **Step 4: Route `handleCreateBlock` through the dialog**

Replace the immediate mutate at `WeekGridPage.tsx:366-371`:

```tsx
const [pendingRange, setPendingRange] = useState<GridRange | null>(null)

function handleCreateBlock(range: GridRange) {
  setPendingRange(range)
}

const createSeries = useMutation({
  mutationFn: (body: CreateAvailabilitySeriesRequest) => createAvailabilitySeries(body),
  onSuccess: () => invalidateAvailabilityQueries(queryClient),
})

function confirmCommit(recurrence: 'once' | 'weekly') {
  const range = pendingRange
  if (!range) return
  setPendingRange(null)

  if (recurrence === 'once') {
    createBlock.mutate({
      startTime: fakeLocalToInstant(range.start, timeZone),
      endTime: fakeLocalToInstant(range.end, timeZone),
    })
    return
  }

  const durationMinutes = Math.round(
    (range.end.getTime() - range.start.getTime()) / 60000,
  )
  createSeries.mutate({
    dayOfWeek: range.start.getUTCDay(),
    startTime: `${String(range.start.getUTCHours()).padStart(2, '0')}:${String(
      range.start.getUTCMinutes(),
    ).padStart(2, '0')}`,
    durationMinutes,
    effectiveFrom: range.start.toISOString().slice(0, 10),
    effectiveUntil: null,
  })
}
```

The fake-local convention means `getUTCHours()` already yields the viewer's local clock hour — do not convert again. Read the comment at `WeekGridPage.tsx:51` before touching this.

- [ ] **Step 5: Add the i18n keys**

`en`:
```json
"commit": {
  "title": "Add this availability?",
  "recurrenceLegend": "How often",
  "once": "Just this time",
  "weekly": "Repeats weekly",
  "save": "Save",
  "discard": "Discard",
  "savedOnce": "Availability added.",
  "savedWeekly": "Weekly availability added.",
  "undo": "Undo"
}
```

`pt-BR`:
```json
"commit": {
  "title": "Adicionar esta disponibilidade?",
  "recurrenceLegend": "Com que frequência",
  "once": "Só desta vez",
  "weekly": "Repete toda semana",
  "save": "Salvar",
  "discard": "Descartar",
  "savedOnce": "Disponibilidade adicionada.",
  "savedWeekly": "Disponibilidade semanal adicionada.",
  "undo": "Desfazer"
}
```

`es`:
```json
"commit": {
  "title": "¿Agregar esta disponibilidad?",
  "recurrenceLegend": "Con qué frecuencia",
  "once": "Solo esta vez",
  "weekly": "Se repite cada semana",
  "save": "Guardar",
  "discard": "Descartar",
  "savedOnce": "Disponibilidad agregada.",
  "savedWeekly": "Disponibilidad semanal agregada.",
  "undo": "Deshacer"
}
```

- [ ] **Step 6: Add the undo toast**

On each mutation's `onSuccess`, show a toast using `components/ui/toast.tsx` with an Undo action that deletes what was just created — `deleteAvailabilityBlock(created.blockId)` or `deleteAvailabilitySeries(created.seriesId)`. Both create helpers already return the created resource; read their return types in `availabilityQueries.ts` and use the real field names.

Add a test:

```tsx
it('undoes a just-created weekly series_D009', async () => {
  renderPage()
  await paintRange()
  fireEvent.click(await screen.findByRole('radio', { name: 'Repeats weekly' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  fireEvent.click(await screen.findByRole('button', { name: 'Undo' }))

  await waitFor(() =>
    expect(api.del).toHaveBeenCalledWith(
      '/api/v1/me/availability/series/{seriesId}',
      expect.anything(),
    ),
  )
})
```

- [ ] **Step 7: Run the tests**

Run: `cd web && npx vitest run src/features/availability/ && npm run lint && npm run build`
Expected: PASS, lint clean, build succeeds.

- [ ] **Step 8: Commit**

```bash
git add web/src/features/availability web/src/i18n
git commit -m "feat(availability): recurrence choice and undo on commit (D-009)

handleCreateBlock always called createAvailabilityBlock, and
createAvailabilitySeries was called from nowhere in the UI, so weekly
availability could not be created at all -- contradicting D-009 ('one-time and
recurring') and section 7 ('then choosing one-time or weekly recurrence').

Releasing a drag now opens a commit step showing the range with a one-time /
repeats-weekly choice. The step doubles as the save confirmation, and
dismissing it discards the drag without an API call. After saving, a toast
offers Undo, deleting what was just created.

Creation costs one extra step, accepted over silent saves that could not
recur, undo, or be trusted."
```

---

### Task 5: Drag to resize and move an existing window

**Files:**
- Modify: `web/src/features/availability/WeekGrid.tsx`
- Modify: `web/src/features/availability/WeekGridPage.tsx` (route the result through `OccurrenceScopeDialog`)
- Modify: `web/src/i18n/{en,pt-BR,es}/availability.json`
- Test: `web/src/features/availability/WeekGridPage.test.tsx`

**Interfaces:**
- Consumes: `onEditOccurrence(occurrenceId)` (existing), the selection state added here, and `OccurrenceScopeDialog`'s existing props.
- Produces: a new `WeekGrid` prop

```tsx
onAdjustOccurrence: (adjustment: {
  occurrenceId: string
  start: Date
  end: Date
}) => void
```

fired on release of a resize or move, in the fake-local convention.

- [ ] **Step 1: Write the failing tests**

```tsx
it('resizes a selected block by dragging its edge', async () => {
  renderPage()
  const block = await screen.findByTestId('occurrence-block-1')

  fireEvent.click(block)
  const handle = await screen.findByTestId('resize-end-block-1')
  fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0 })
  fireEvent.pointerMove(handle, { pointerType: 'mouse', clientY: 200 })
  fireEvent.pointerUp(handle, { pointerType: 'mouse' })

  await waitFor(() => expect(api.patch).toHaveBeenCalled())
})

it('shows no resize handles until the block is selected', async () => {
  renderPage()
  await screen.findByTestId('occurrence-block-1')

  expect(screen.queryByTestId('resize-end-block-1')).not.toBeInTheDocument()
})

it('routes a resize on a recurring occurrence through the scope dialog_D025', async () => {
  renderPage()
  const occurrence = await screen.findByTestId('occurrence-series-1')

  fireEvent.click(occurrence)
  const handle = await screen.findByTestId('resize-end-series-1')
  fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0 })
  fireEvent.pointerMove(handle, { pointerType: 'mouse', clientY: 200 })
  fireEvent.pointerUp(handle, { pointerType: 'mouse' })

  expect(await screen.findByRole('dialog')).toHaveTextContent('Only this occurrence')
  expect(api.patch).not.toHaveBeenCalled()
})
```

Use the `data-testid` names the grid actually renders — read the existing occurrence markup and follow its convention; if occurrences carry no testid today, add ones shaped like the above.

- [ ] **Step 2: Run and verify they fail**

Run: `cd web && npx vitest run src/features/availability/WeekGridPage.test.tsx -t "resize"`
Expected: FAIL — no handles exist and no adjust callback fires.

- [ ] **Step 3: Add selection state and handles to `WeekGrid`**

Track `const [selectedOccurrenceId, setSelectedOccurrenceId] = useState<string | null>(null)`; set it in the existing click/tap path that already calls `onEditOccurrence` — activation both opens the editor and selects. Render two absolutely-positioned handles (top and bottom edges) **only** on the selected occurrence, each `min-h-11` for touch, each with an accessible name from i18n (`grid.resizeStart` / `grid.resizeEnd`).

- [ ] **Step 4: Implement the drag**

On `pointerdown` on a handle: `event.currentTarget.setPointerCapture(event.pointerId)`, record the origin. On `pointermove`: compute the slot delta from `clientY` against the grid's slot height and snap to `SLOT_MINUTES`. On `pointerup`: release capture and call `onAdjustOccurrence` with the new start/end.

The handle's own `pointerdown` must `stopPropagation()` so it never reaches the cell's paint handlers, and the handles must not start the long-press paint timer. The grid-level `touch-action` gating stays exactly as it is — the handle sets `touch-action: none` on **itself** only.

- [ ] **Step 5: Route the result in `WeekGridPage`**

```tsx
function handleAdjustOccurrence(adjustment: {
  occurrenceId: string
  start: Date
  end: Date
}) {
  const raw = (calendar.data?.myAvailability.occurrences ?? []).find(
    (occurrence) => occurrenceKey(occurrence) === adjustment.occurrenceId,
  )
  if (!raw) return

  setBlockEditorValue({
    start: datetimeLocalValue(adjustment.start),
    end: datetimeLocalValue(adjustment.end),
  })

  if (raw.origin.kind === 'occurrence' && raw.origin.seriesId && raw.origin.occurrenceDate) {
    setEditingTarget({
      kind: 'occurrence',
      seriesId: raw.origin.seriesId,
      occurrenceDate: raw.origin.occurrenceDate,
    })
    setPendingScope('onlyThis')
    setScopeDialogOpen(true)
    return
  }

  if (raw.origin.blockId) {
    setEditingTarget({ kind: 'block', blockId: raw.origin.blockId })
    editBlock.mutate({
      blockId: raw.origin.blockId,
      startTime: fakeLocalToInstant(adjustment.start, timeZone),
      endTime: fakeLocalToInstant(adjustment.end, timeZone),
    })
  }
}
```

A recurring occurrence **must** go through the scope dialog — committing a drag straight to the API would let an edge-nudge silently rewrite a whole series. Reuse `occurrenceKey`/`datetimeLocalValue` exactly as the existing `handleEditOccurrence` builds them; if those are inline today, extract them rather than duplicating the logic.

- [ ] **Step 6: Add the i18n keys**

`en`: `"grid": { "resizeStart": "Change start time", "resizeEnd": "Change end time" }`
`pt-BR`: `"grid": { "resizeStart": "Alterar horário de início", "resizeEnd": "Alterar horário de término" }`
`es`: `"grid": { "resizeStart": "Cambiar la hora de inicio", "resizeEnd": "Cambiar la hora de fin" }`

Merge into the existing `grid` block if one already exists rather than creating a duplicate key.

- [ ] **Step 7: Run the tests**

Run: `cd web && npx vitest run src/features/availability/ && npm run lint && npm run build`
Expected: PASS. Re-run the touch regression arms from `e1a3dd2` explicitly:
`npx vitest run src/features/availability/ -t "P5TOUCH01"` — these must still pass. If a paint-drag or scroll test broke, the handles are stealing gestures; fix the handle scoping, not the test.

- [ ] **Step 8: Commit**

```bash
git add web/src/features/availability web/src/i18n
git commit -m "feat(availability): resize and move an existing window by pointer

Adjusting a published window meant opening the editor and retyping times.
Selecting a block now reveals edge handles that resize it against the same
15-minute grid.

Handles appear only on a selected block. A bare finger-drag across the grid
must stay a page scroll, so permanent edge handles would turn every block into
a scroll trap; the handle sets touch-action on itself alone and the grid-level
gating that P5-TOUCH-01 depends on is untouched. Pointer capture holds the
gesture for its duration.

A resize on a recurring occurrence routes through OccurrenceScopeDialog rather
than committing straight to the API, so an edge-nudge cannot silently rewrite a
whole series -- D-025 keeps 'only this occurrence' as the default."
```

---

## Final verification

- [ ] `cd web && npm run lint && npm test && npm run build` — full web suite green.
- [ ] `dotnet build PartyUp.sln` — 0 warnings.
- [ ] Targeted integration filter from Task 1 green.
- [ ] Update `TRACEABILITY.md`: D-009 and D-025 gain their client tests (foreman owns this file — report the rows rather than editing if you are a worker).
- [ ] The full WSL suite and the browser lane are the owner's gate before push, not part of these tasks.
