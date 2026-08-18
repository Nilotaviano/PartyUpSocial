import { GripHorizontal } from "lucide-react";
import { cn } from "./utils";
import type { GridGeometry } from "./gridEditing";

/**
 * Presentational pieces every week-shaped grid shares. They carry no feature
 * vocabulary — a poll grid uses them with the same geometry a calendar does.
 */

/**
 * The decorative gridline layer of one day column. The lines are real elements
 * positioned by the same `top()` the hour labels use, never a
 * `repeating-linear-gradient`: iOS Safari rounds each gradient tile
 * independently at fractional device pixel ratios, so the painted spacing
 * drifts away from the labels and bands down the column.
 */
export function GridLines({ geometry }: { geometry: GridGeometry }) {
  return (
    <div aria-hidden="true" className="pointer-events-none absolute inset-0">
      {geometry.lines.map((line) => (
        <div
          key={line.m}
          className="absolute inset-x-0 h-px"
          data-gridline={line.hour ? "hour" : "quarter"}
          style={{
            top: geometry.top(line.m),
            background: line.hour ? "var(--grid-hour)" : "var(--grid-line)",
          }}
        />
      ))}
    </div>
  );
}

/**
 * A pointer resize affordance with a keyboard equivalent: the arrows nudge the
 * edge by one step, and Enter/Space are swallowed so the handle cannot re-fire
 * the selection of the block it sits on (the block's own keydown handler is an
 * ancestor of this button).
 */
export function ResizeHandle({
  position,
  label,
  stepMin,
  onNudge,
  onPointerDown,
}: {
  position: "top" | "bottom";
  label: string;
  stepMin: number;
  onNudge: (delta: number) => void;
  onPointerDown: (e: React.PointerEvent<HTMLElement>) => void;
}) {
  return (
    <button
      type="button"
      onPointerDown={onPointerDown}
      onKeyDown={(e) => {
        if (e.key === "ArrowUp" || e.key === "ArrowDown") {
          e.preventDefault();
          onNudge(e.key === "ArrowUp" ? -stepMin : stepMin);
          return;
        }
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          e.stopPropagation();
        }
      }}
      aria-label={label}
      className={cn(
        "absolute left-1/2 z-30 grid h-6 w-11 -translate-x-1/2 cursor-ns-resize touch-none place-items-center rounded-full border-2 border-ring bg-card shadow-[var(--shadow-card)]",
        position === "top" ? "-top-3" : "-bottom-3",
      )}
    >
      <GripHorizontal className="size-3.5 text-muted-foreground" />
    </button>
  );
}
