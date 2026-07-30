import { useRef, useState } from "react";
import { LiveRegion, useAnnouncer } from "./announcer";
import { EditDialog } from "./EditDialog";
import { Block, DAYS, SLOTS_PER_DAY, clamp, newId, rangeLabel, slotToTime } from "./lib";

const SLOT_H = 14; // px per 15-min row

interface Sel {
  day: number;
  anchor: number;
  head: number;
}

function selRange(s: Sel): { start: number; end: number } {
  return { start: Math.min(s.anchor, s.head), end: Math.max(s.anchor, s.head) + 1 };
}

export default function CustomGridPage() {
  const [blocks, setBlocks] = useState<Block[]>([]);
  const [focused, setFocused] = useState({ day: 0, slot: 36 }); // 09:00 Monday
  const [sel, setSel] = useState<Sel | null>(null); // keyboard selection
  const [drag, setDrag] = useState<Sel | null>(null); // pointer selection
  const [editing, setEditing] = useState<Block | null>(null);
  const [msg, announce] = useAnnouncer();
  const gridRef = useRef<HTMLDivElement>(null);
  const cellRefs = useRef(new Map<string, HTMLDivElement>());

  const focusCell = (day: number, slot: number) => {
    setFocused({ day, slot });
    const el = cellRefs.current.get(`${day}:${slot}`);
    el?.focus();
    el?.scrollIntoView({ block: "nearest" });
  };

  const createBlock = (day: number, start: number, end: number) => {
    setBlocks((bs) => [...bs, { id: newId(), day, startSlot: start, endSlot: end }]);
    announce(`Availability block created, ${rangeLabel(day, start, end)}`);
  };

  const deleteBlock = (b: Block) => {
    setBlocks((bs) => bs.filter((x) => x.id !== b.id));
    announce(`Availability block deleted, ${rangeLabel(b.day, b.startSlot, b.endSlot)}`);
    focusCell(b.day, b.startSlot);
  };

  // ---- keyboard ----

  const onGridKeyDown = (e: React.KeyboardEvent) => {
    const { day, slot } = focused;

    const move = (dd: number, ds: number) => {
      e.preventDefault();
      if (e.shiftKey) {
        if (dd !== 0) return; // selection is single-day; ignore horizontal extension
        const base = sel ?? { day, anchor: slot, head: slot };
        const next = { ...base, head: clamp(base.head + ds, 0, SLOTS_PER_DAY - 1) };
        setSel(next);
        const { start, end } = selRange(next);
        announce(`Selecting ${rangeLabel(next.day, start, end)}`);
        focusCell(next.day, next.head);
      } else {
        setSel(null);
        focusCell(clamp(day + dd, 0, 6), clamp(slot + ds, 0, SLOTS_PER_DAY - 1));
      }
    };

    switch (e.key) {
      case "ArrowUp":
        move(0, -1);
        break;
      case "ArrowDown":
        move(0, 1);
        break;
      case "ArrowLeft":
        move(-1, 0);
        break;
      case "ArrowRight":
        move(1, 0);
        break;
      case "PageUp":
        move(0, -4);
        break;
      case "PageDown":
        move(0, 4);
        break;
      case "Enter":
        if (sel) {
          e.preventDefault();
          const { start, end } = selRange(sel);
          createBlock(sel.day, start, end);
          setSel(null);
        }
        break;
      case "Escape":
        if (sel) {
          setSel(null);
          announce("Selection cancelled");
        }
        break;
    }
  };

  // ---- pointer (mouse + touch via Pointer Events) ----

  const posFromEvent = (e: React.PointerEvent): { day: number; slot: number } => {
    const rect = gridRef.current!.getBoundingClientRect();
    const day = clamp(Math.floor(((e.clientX - rect.left) / rect.width) * 7), 0, 6);
    const slot = clamp(Math.floor((e.clientY - rect.top) / SLOT_H), 0, SLOTS_PER_DAY - 1);
    return { day, slot };
  };

  const onPointerDown = (e: React.PointerEvent) => {
    if ((e.target as HTMLElement).closest(".cg-block")) return;
    if (e.pointerType === "mouse" && e.button !== 0) return;
    e.preventDefault();
    gridRef.current!.setPointerCapture(e.pointerId);
    const p = posFromEvent(e);
    const d = { day: p.day, anchor: p.slot, head: p.slot };
    setDrag(d);
    const { start, end } = selRange(d);
    announce(`Selecting ${rangeLabel(d.day, start, end)}`);
  };

  const onPointerMove = (e: React.PointerEvent) => {
    if (!drag) return;
    const p = posFromEvent(e); // day clamped to anchor's day: selection is single-day
    if (p.slot !== drag.head) {
      const next = { ...drag, head: p.slot };
      setDrag(next);
      const { start, end } = selRange(next);
      announce(`Selecting ${rangeLabel(next.day, start, end)}`);
    }
  };

  const onPointerUp = () => {
    if (!drag) return;
    const { start, end } = selRange(drag);
    createBlock(drag.day, start, end);
    setFocused({ day: drag.day, slot: drag.head });
    setDrag(null);
  };

  const onPointerCancel = () => setDrag(null);

  // ---- render ----

  const active = drag ?? sel;
  const rows = [];
  for (let slot = 0; slot < SLOTS_PER_DAY; slot++) {
    const cells = [];
    for (let day = 0; day < 7; day++) {
      const isFocused = focused.day === day && focused.slot === slot;
      const inSel =
        active !== null &&
        active.day === day &&
        slot >= Math.min(active.anchor, active.head) &&
        slot <= Math.max(active.anchor, active.head);
      const key = `${day}:${slot}`;
      cells.push(
        <div
          key={key}
          role="gridcell"
          ref={(el) => {
            if (el) cellRefs.current.set(key, el);
            else cellRefs.current.delete(key);
          }}
          tabIndex={isFocused ? 0 : -1}
          aria-label={`${DAYS[day]} ${slotToTime(slot)}`}
          aria-selected={inSel ? true : undefined}
          className={
            "cg-cell" + (slot % 4 === 3 ? " cg-hour-end" : "") + (inSel ? " cg-sel" : "")
          }
          onFocus={() => setFocused({ day, slot })}
        />,
      );
    }
    rows.push(
      <div key={slot} role="row" className="cg-row">
        {cells}
      </div>,
    );
  }

  const hourLabels = [];
  for (let h = 0; h < 24; h++) {
    hourLabels.push(
      <div key={h} className="cg-hourlabel">
        {String(h).padStart(2, "0")}:00
      </div>,
    );
  }

  return (
    <div className="page">
      <h1>Custom grid</h1>
      <p className="hint">
        Keyboard: arrows move, Shift+Up/Down selects, Enter creates, Escape cancels. On a block:
        Enter/E edits, Delete removes. Pointer: drag on the grid (mouse or touch).
      </p>
      <LiveRegion message={msg} />
      <div className="cg-scroll">
        <div className="cg-days" aria-hidden="true">
          <div className="cg-gutter" />
          {DAYS.map((d) => (
            <div key={d} className="cg-dayname">
              {d.slice(0, 3)}
            </div>
          ))}
        </div>
        <div className="cg-body">
          <div className="cg-times" aria-hidden="true">
            {hourLabels}
          </div>
          <div className="cg-gridwrap">
            <div
              role="grid"
              aria-label="Weekly availability grid, 15 minute slots"
              className="cg-grid"
              ref={gridRef}
              onKeyDown={onGridKeyDown}
              onPointerDown={onPointerDown}
              onPointerMove={onPointerMove}
              onPointerUp={onPointerUp}
              onPointerCancel={onPointerCancel}
            >
              {rows}
            </div>
            <div className="cg-overlay">
              {blocks.map((b) => (
                <button
                  key={b.id}
                  type="button"
                  className="cg-block"
                  style={{
                    top: b.startSlot * SLOT_H,
                    height: (b.endSlot - b.startSlot) * SLOT_H,
                    left: `${(b.day / 7) * 100}%`,
                    width: `${100 / 7}%`,
                  }}
                  aria-label={`Availability block, ${rangeLabel(b.day, b.startSlot, b.endSlot)}. Press Enter to edit, Delete to remove.`}
                  onClick={() => setEditing(b)}
                  onKeyDown={(e) => {
                    if (e.key === "Delete" || e.key === "Backspace") {
                      e.preventDefault();
                      deleteBlock(b);
                    } else if (e.key.toLowerCase() === "e") {
                      setEditing(b);
                    }
                  }}
                >
                  <span aria-hidden="true">
                    {slotToTime(b.startSlot)}–{slotToTime(b.endSlot)}
                  </span>
                </button>
              ))}
            </div>
          </div>
        </div>
      </div>
      <EditDialog
        block={editing}
        onSave={(s, en) => {
          if (!editing) return;
          setBlocks((bs) =>
            bs.map((x) => (x.id === editing.id ? { ...x, startSlot: s, endSlot: en } : x)),
          );
          announce(`Availability block updated, ${rangeLabel(editing.day, s, en)}`);
          setEditing(null);
          focusCell(editing.day, s);
        }}
        onDelete={() => {
          if (!editing) return;
          const b = editing;
          setEditing(null);
          deleteBlock(b);
        }}
        onClose={() => {
          if (editing) focusCell(editing.day, editing.startSlot);
          setEditing(null);
        }}
      />
    </div>
  );
}
