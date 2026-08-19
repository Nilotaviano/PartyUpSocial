import { useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { AvatarDisc } from "../../friends/AvatarDisc";
import { cn } from "./utils";
import { GridLines, ResizeHandle } from "./GridLayers";
import { createGridGeometry, type GridBlock } from "./gridEditing";
import { useGridGestures, type GridBlockPressProps } from "./useGridGestures";
import {
  ROW_H,
  SLOT_MIN,
  bandsForWeek,
  clamp,
  fmt,
  fmtRange,
  type AvailabilityRule,
  type Band,
  type Friend,
  type GameSession,
  type MergedSpan,
  type PollOption,
  type WeekLabels,
} from "./partyup-data";

export type Draft = {
  day: number;
  start: number;
  end: number;
  x: number;
  y: number;
};

type Props = {
  rules: AvailabilityRule[];
  /**
   * The server's own merged ranges (A-076): the busy state the cell layer
   * reports is read from these verbatim, never re-derived from `rules` — the
   * two can legitimately differ and the server's answer is the authority.
   */
  merged: MergedSpan[];
  friends: Friend[];
  sessions: GameSession[];
  polls: PollOption[];
  labels: WeekLabels;
  days: number[];
  rangeStart: number;
  rangeEnd: number;
  todayIndex: number;
  nowMinutes: number | null;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onDraftComplete: (d: Draft) => void;
  onResize: (ruleId: string, start: number, end: number) => void;
  /** Keyboard navigation past the shown day window pages it (CALENDAR §9 S5). */
  onPageStep?: (delta: -1 | 1) => void;
};

export function WeekGrid({
  rules,
  merged,
  friends,
  sessions: allSessions,
  polls: allPolls,
  labels,
  days,
  rangeStart,
  rangeEnd,
  todayIndex,
  nowMinutes,
  selectedId,
  onSelect,
  onDraftComplete,
  onResize,
  onPageStep,
}: Props) {
  const { t } = useTranslation("availability");
  const [liveMessage, setLiveMessage] = useState("");

  const geometry = useMemo(
    () => createGridGeometry({ rangeStart, rangeEnd, slotMin: SLOT_MIN, rowH: ROW_H }),
    [rangeStart, rangeEnd],
  );
  const { pixelHeight: height, rowCount, top, span: hgt, minutesOfRow } = geometry;
  const bands = bandsForWeek(rules);

  const columnsRef = useRef<HTMLDivElement>(null);
  // The calendar edits availability windows in place: it paints new ones and
  // drags their edges, but a whole-band move belongs to the poll grids.
  const gestures = useGridGestures({
    columnsRef,
    rangeStart,
    rangeEnd,
    createStepMin: SLOT_MIN,
    resizeStepMin: SLOT_MIN,
    minDurationMin: SLOT_MIN,
    initialCreateMin: SLOT_MIN,
    moveEnabled: false,
    onCreateStart: (_day, minute, pointerType) => {
      if (pointerType === "touch") {
        setLiveMessage(t("a11y.selectionStarted", { time: fmt(minute) }));
        return;
      }
      onSelect(null);
    },
    onBlockCreate: (day, start, end, at) =>
      onDraftComplete({ day, start, end, x: at.x, y: at.y }),
    onBlockResize: onResize,
    onBlockSelect: onSelect,
  });
  const { drag } = gestures;

  // Roving tab stop over the accessible cell layer (ARIA grid pattern), plus
  // the keyboard selection anchor the Enter/Space path builds a draft from.
  const [focusCell, setFocusCell] = useState({ col: 0, row: 0 });
  const [keyAnchor, setKeyAnchor] = useState<{ col: number; row: number } | null>(null);
  const focusCol = clamp(focusCell.col, 0, Math.max(days.length - 1, 0));
  const focusRow = clamp(focusCell.row, 0, Math.max(rowCount - 1, 0));

  const hours: number[] = [];
  for (let m = rangeStart; m <= rangeEnd; m += 60) hours.push(m);

  const busyAt = (day: number, row: number) => {
    const m = minutesOfRow(row);
    return merged.some((span) => span.day === day && span.start <= m && span.end > m);
  };

  function moveFocus(col: number, row: number) {
    if (col < 0) {
      onPageStep?.(-1);
      return;
    }
    if (col > days.length - 1) {
      onPageStep?.(1);
      return;
    }
    const next = { col, row: clamp(row, 0, rowCount - 1) };
    setFocusCell(next);
    const cell = columnsRef.current
      ?.closest("[data-grid-root]")
      ?.querySelector<HTMLElement>(`[data-cell="${next.col}:${next.row}"]`);
    cell?.focus();
  }

  function onCellKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    const extend = event.shiftKey && keyAnchor !== null;
    switch (event.key) {
      case "ArrowUp":
        event.preventDefault();
        moveFocus(focusCol, focusRow - 1);
        if (extend) announceExtent(clamp(focusRow - 1, 0, rowCount - 1));
        return;
      case "ArrowDown":
        event.preventDefault();
        moveFocus(focusCol, focusRow + 1);
        if (extend) announceExtent(clamp(focusRow + 1, 0, rowCount - 1));
        return;
      case "ArrowLeft":
        event.preventDefault();
        moveFocus(focusCol - 1, focusRow);
        return;
      case "ArrowRight":
        event.preventDefault();
        moveFocus(focusCol + 1, focusRow);
        return;
      case "Home":
        event.preventDefault();
        moveFocus(focusCol, 0);
        return;
      case "End":
        event.preventDefault();
        moveFocus(focusCol, rowCount - 1);
        return;
      case "Escape":
        if (keyAnchor) {
          event.preventDefault();
          setKeyAnchor(null);
          setLiveMessage(t("a11y.selectionCanceled"));
        }
        return;
      case "Enter":
      case " ":
        event.preventDefault();
        commitKeyboardStep();
        return;
      default:
    }
  }

  function announceExtent(row: number) {
    if (!keyAnchor) return;
    const a = Math.min(keyAnchor.row, row);
    const b = Math.max(keyAnchor.row, row) + 1;
    setLiveMessage(
      t("a11y.selectionExtent", { start: fmt(minutesOfRow(a)), end: fmt(minutesOfRow(b)) }),
    );
  }

  function commitKeyboardStep() {
    const day = days[focusCol];
    if (day === undefined) return;
    if (!keyAnchor || keyAnchor.col !== focusCol) {
      setKeyAnchor({ col: focusCol, row: focusRow });
      setLiveMessage(t("a11y.selectionStarted", { time: fmt(minutesOfRow(focusRow)) }));
      return;
    }
    const a = minutesOfRow(Math.min(keyAnchor.row, focusRow));
    const b = minutesOfRow(Math.max(keyAnchor.row, focusRow) + 1);
    setKeyAnchor(null);
    setLiveMessage(t("a11y.selectionReady", { start: fmt(a), end: fmt(b) }));
    const cell = columnsRef.current
      ?.closest("[data-grid-root]")
      ?.querySelector<HTMLElement>(`[data-cell="${focusCol}:${focusRow}"]`);
    const rect = cell?.getBoundingClientRect();
    onDraftComplete({
      day,
      start: a,
      end: b,
      x: rect ? rect.left + rect.width / 2 : 0,
      y: rect ? rect.top : 0,
    });
  }

  const keyPreview =
    keyAnchor && days[keyAnchor.col] !== undefined && keyAnchor.col === focusCol
      ? {
          day: days[focusCol]!,
          a: minutesOfRow(Math.min(keyAnchor.row, focusRow)),
          b: minutesOfRow(Math.max(keyAnchor.row, focusRow) + 1),
        }
      : null;

  const gridTemplate = `56px repeat(${days.length}, minmax(0,1fr))`;

  return (
    <div className="relative" data-grid-root data-touch-dragging={gestures.active}>
      {/* header row */}
      <div
        className="sticky top-0 z-30 -mx-1 bg-background/92 px-1 pt-2 pb-2 backdrop-blur-md"
        style={{ gridTemplateColumns: gridTemplate }}
      >
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }}>
          <div />
          {days.map((d) => {
            const isToday = d === todayIndex;
            return (
              <div
                key={d}
                className={cn(
                  "flex min-w-0 flex-col items-center rounded-xl border px-1 py-1.5 text-center",
                  isToday
                    ? "border-primary/60 bg-primary/15"
                    : "border-border/70 bg-card/80",
                )}
              >
                <span className="font-display text-[13px] leading-tight font-bold">
                  {labels.dayNames[d]}
                </span>
                <span className="truncate text-[11px] text-muted-foreground">
                  {labels.dayDates[d]}
                </span>
              </div>
            );
          })}
        </div>
      </div>

      {/* grid body */}
      <div className="relative mt-1">
        <div
          className="grid gap-1.5"
          style={{ gridTemplateColumns: gridTemplate }}
          ref={columnsRef}
        >
          {/* hour ruler */}
          <div className="relative" style={{ height }}>
            {hours.map((m) => (
              <div
                key={m}
                className="absolute right-2 -translate-y-1/2 font-display text-[11px] font-semibold text-muted-foreground tabular-nums"
                style={{ top: top(m) }}
              >
                {fmt(m)}
              </div>
            ))}
          </div>

          {days.map((day) => {
            const dayBands = bands.filter((b) => b.day === day);
            const sessions = allSessions.filter((s) => s.day === day);
            const polls = allPolls.filter((p) => p.day === day);
            const friendWindows = friends
              .flatMap((f) => f.windows.filter((w) => w.day === day).map((w) => ({ ...w, friend: f })))
              .sort((a, b) => a.start - b.start);

            // group friend chips into 30-min buckets at window starts
            const buckets = new Map<number, typeof friendWindows>();
            for (const w of friendWindows) {
              const key = Math.floor(w.start / 30) * 30;
              const arr = buckets.get(key) ?? [];
              arr.push(w);
              buckets.set(key, arr);
            }

            const isToday = day === todayIndex;
            const livePaint = gestures.previewFor(day);
            const paintPreview = livePaint
              ? livePaint.mode === "create"
                ? { a: livePaint.a, b: livePaint.b }
                : null
              : keyPreview && keyPreview.day === day
                ? { a: keyPreview.a, b: keyPreview.b }
                : null;

            return (
              <div
                key={day}
                {...gestures.columnProps(day)}
                className={cn(
                  "relative overflow-hidden rounded-2xl border bg-card select-none",
                  isToday ? "border-primary/40" : "border-border/70",
                  gestures.active ? "touch-none" : "touch-pan-y",
                )}
                style={{ height }}
              >
                <GridLines geometry={geometry} />

                {/* my availability bands */}
                {dayBands.map((b) => {
                  const block: GridBlock = {
                    id: b.ruleId,
                    day,
                    startMin: b.start,
                    endMin: b.end,
                  };
                  return (
                    <BandBlock
                      key={`${b.ruleId}-${b.day}`}
                      band={b}
                      top={top(b.start)}
                      height={hgt(b.start, b.end)}
                      selected={selectedId === b.ruleId}
                      pressProps={gestures.blockPressProps(block)}
                      onSelect={() => onSelect(b.ruleId)}
                      startHandle={gestures.resizeHandleProps(block, "start")}
                      endHandle={gestures.resizeHandleProps(block, "end")}
                      onNudge={(edge, delta) => {
                        const start = edge === "start" ? Math.min(b.start + delta, b.end - SLOT_MIN) : b.start;
                        const end = edge === "end" ? Math.max(b.end + delta, b.start + SLOT_MIN) : b.end;
                        onResize(b.ruleId, clamp(start, rangeStart, rangeEnd), clamp(end, rangeStart, rangeEnd));
                      }}
                      live={
                        drag && drag.mode === "resize" && drag.blockId === b.ruleId
                          ? { start: drag.a, end: drag.b }
                          : null
                      }
                      topOf={top}
                      heightOf={hgt}
                    />
                  );
                })}

                {/* friends' chips */}
                {[...buckets.entries()].map(([startKey, group]) => {
                  const shown = group.slice(0, 3);
                  const rest = group.length - shown.length;
                  return (
                    <div
                      key={startKey}
                      className="pointer-events-none absolute left-1.5 z-20 flex items-center gap-0.5"
                      style={{ top: top(group[0]!.start) + 19 }}
                    >
                      {shown.map((w) => (
                        <span
                          key={w.friend.id}
                          title={`${w.friend.name} · ${fmtRange(w.start, w.end)}`}
                          className={cn(
                            "grid place-items-center rounded-full",
                            w.intent === "maybe" && "opacity-70 ring-1 ring-dashed",
                          )}
                        >
                          <AvatarDisc
                            seed={w.friend.id}
                            emoji={w.friend.emoji}
                            imgUrl={w.friend.imgUrl}
                            masked={w.friend.masked}
                            size="xxs"
                          />
                          {/* The disc is aria-hidden decoration and `title` is
                              mouse-only, so a screen reader viewer otherwise gets
                              nothing for this chip. */}
                          <span className="sr-only">
                            {`${w.friend.name} · ${fmtRange(w.start, w.end)}`}
                          </span>
                        </span>
                      ))}
                      {rest > 0 && (
                        <span className="grid h-[19px] min-w-[19px] place-items-center rounded-full border border-border bg-muted px-1 text-[9px] font-bold text-muted-foreground">
                          +{rest}
                        </span>
                      )}
                    </div>
                  );
                })}

                {/* scheduled sessions */}
                {sessions.map((s) => (
                  <div
                    key={s.id}
                    className="pointer-events-none absolute right-1.5 left-[38%] z-20 overflow-hidden rounded-xl border-2 border-foreground/70 bg-accent px-1.5 py-1 text-accent-foreground shadow-[var(--shadow-card)]"
                    style={{ top: top(s.start) + 2, minHeight: 26, height: hgt(s.start, s.end) - 4 }}
                  >
                    <p className="truncate font-display text-[11px] leading-tight font-extrabold">
                      🎮 {s.title}
                    </p>
                    <p className="truncate text-[10px] leading-tight font-semibold opacity-80">
                      {fmt(s.start)} · {t("lovable.grid.inCount", { count: s.occupancy })}
                    </p>
                  </div>
                ))}

                {/* poll options */}
                {polls.map((p) => (
                  <div
                    key={p.id}
                    className="pointer-events-none absolute right-1.5 left-[38%] z-20 overflow-hidden rounded-xl border-2 border-dashed border-foreground/45 bg-card/85 px-1.5 py-1"
                    style={{ top: top(p.start) + 2, minHeight: 26, height: hgt(p.start, p.end) - 4 }}
                  >
                    <p className="truncate font-display text-[11px] leading-tight font-extrabold">
                      ? {p.pollTitle}
                    </p>
                    <p className="truncate text-[10px] leading-tight font-semibold text-muted-foreground">
                      {t("lovable.grid.pollPill", { vote: p.voteLabel })}
                    </p>
                  </div>
                ))}

                {/* live drag preview */}
                {paintPreview && (
                  <div
                    className="pointer-events-none absolute inset-x-1 z-30 rounded-xl border-2 border-primary bg-primary/25 px-2 py-1"
                    style={{
                      top: top(paintPreview.a),
                      height: Math.max(hgt(paintPreview.a, paintPreview.b), 16),
                    }}
                  >
                    <span className="font-display text-[11px] font-bold tabular-nums">
                      {fmtRange(paintPreview.a, paintPreview.b)}
                    </span>
                  </div>
                )}

                {/* now line */}
                {isToday && nowMinutes !== null && nowMinutes >= rangeStart && nowMinutes <= rangeEnd && (
                  <div
                    className="pointer-events-none absolute inset-x-0 z-40 flex items-center"
                    style={{ top: top(nowMinutes) }}
                  >
                    <span className="size-2 rounded-full bg-destructive" />
                    <span className="h-px flex-1 bg-destructive" />
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {/* Keyboard/AT layer: the painted columns above are pointer-driven, so
            the same 15-minute slots are also exposed as a real ARIA grid.
            It is transparent and pointer-transparent — focus, roles and
            accessible names only, no pixels of its own. */}
        <div
          aria-colcount={days.length + 1}
          aria-label={t("a11y.gridLabel")}
          aria-rowcount={rowCount}
          className="pointer-events-none absolute inset-0 grid"
          role="grid"
          style={{ gridTemplateRows: `repeat(${rowCount}, ${ROW_H}px)` }}
          onKeyDown={onCellKeyDown}
        >
          {Array.from({ length: rowCount }, (_, row) => (
            <div
              key={row}
              aria-rowindex={row + 1}
              className="grid gap-1.5"
              role="row"
              style={{ gridTemplateColumns: gridTemplate }}
            >
              {/* The hiding lives on an inner span, never on the grid item
                  itself: `sr-only` is `position: absolute`, which would take
                  the row header out of the row's column flow and shift every
                  day cell one track left, off the column it names. */}
              <div aria-colindex={1} role="rowheader">
                <span className="sr-only">{fmt(minutesOfRow(row))}</span>
              </div>
              {days.map((day, col) => {
                const covering = busyAt(day, row);
                const isFocus = col === focusCol && row === focusRow;
                return (
                  <div
                    key={day}
                    aria-colindex={col + 2}
                    aria-label={`${labels.dayNames[day]} ${fmt(minutesOfRow(row))}${
                      covering ? ` – ${t("a11y.busy")}` : ""
                    }`}
                    aria-selected={
                      keyAnchor !== null && keyAnchor.col === col
                        ? row >= Math.min(keyAnchor.row, focusRow) &&
                          row <= Math.max(keyAnchor.row, focusRow)
                        : undefined
                    }
                    className="rounded-sm outline-focus focus-visible:outline-2 focus-visible:outline-offset-1"
                    data-busy={covering ? "true" : undefined}
                    data-cell={`${col}:${row}`}
                    data-state={covering ? "busy" : "free"}
                    role="gridcell"
                    tabIndex={isFocus ? 0 : -1}
                    onFocus={() => setFocusCell({ col, row })}
                  />
                );
              })}
            </div>
          ))}
        </div>
      </div>

      <div aria-live="polite" className="sr-only" role="status">
        {liveMessage}
      </div>
    </div>
  );
}

function BandBlock({
  band,
  top,
  height,
  selected,
  pressProps,
  onSelect,
  startHandle,
  endHandle,
  onNudge,
  live,
  topOf,
  heightOf,
}: {
  band: Band;
  top: number;
  height: number;
  selected: boolean;
  pressProps: GridBlockPressProps;
  onSelect: () => void;
  startHandle: { onPointerDown: (e: React.PointerEvent<HTMLElement>) => void };
  endHandle: { onPointerDown: (e: React.PointerEvent<HTMLElement>) => void };
  onNudge: (edge: "start" | "end", delta: number) => void;
  live: { start: number; end: number } | null;
  topOf: (m: number) => number;
  heightOf: (a: number, b: number) => number;
}) {
  const { t } = useTranslation("availability");
  const tOf = live ? topOf(live.start) : top;
  const h = live ? heightOf(live.start, live.end) : height;
  const start = live ? live.start : band.start;
  const end = live ? live.end : band.end;

  return (
    <div
      data-band={band.ruleId}
      tabIndex={0}
      {...pressProps}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onSelect();
        }
      }}
      className={cn(
        "absolute inset-x-1 z-10 rounded-xl px-2 py-1 text-left transition-shadow",
        band.intent === "free"
          ? "bg-free text-free-foreground"
          : "bg-maybe text-maybe-foreground",
        band.intent === "maybe" && "border-2 border-dashed border-current/40",
        selected && "shadow-[var(--shadow-pop)] ring-2 ring-ring",
      )}
      style={{ top: tOf, height: Math.max(h, 18) }}
    >
      <span className="font-display text-[11px] leading-tight font-bold tabular-nums">
        {fmtRange(start, end)}
      </span>
      {h > 44 && (
        <span className="block text-[10px] leading-tight font-semibold opacity-90">
          {band.kind === "recurring" ? t("lovable.grid.weekly") : t("lovable.grid.oneTime")}
        </span>
      )}
      {selected && (
        <>
          <ResizeHandle
            position="top"
            label={t("grid.resizeStart")}
            stepMin={SLOT_MIN}
            onPointerDown={startHandle.onPointerDown}
            onNudge={(delta) => onNudge("start", delta)}
          />
          <ResizeHandle
            position="bottom"
            label={t("grid.resizeEnd")}
            stepMin={SLOT_MIN}
            onPointerDown={endHandle.onPointerDown}
            onNudge={(delta) => onNudge("end", delta)}
          />
        </>
      )}
    </div>
  );
}
