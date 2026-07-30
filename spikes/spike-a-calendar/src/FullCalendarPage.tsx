import { useEffect, useRef, useState } from "react";
import FullCalendar from "@fullcalendar/react";
import timeGridPlugin from "@fullcalendar/timegrid";
import interactionPlugin from "@fullcalendar/interaction";
import type { DateSelectArg, DateSpanApi, EventClickArg } from "@fullcalendar/core";
import { LiveRegion, useAnnouncer } from "./announcer";
import { EditDialog } from "./EditDialog";
import {
  Block,
  SLOTS_PER_DAY,
  clamp,
  dateToDaySlot,
  newId,
  rangeLabel,
  slotToDate,
  weekStart,
} from "./lib";

interface Sel {
  day: number;
  anchor: number;
  head: number;
}

function selRange(s: Sel): { start: number; end: number } {
  return { start: Math.min(s.anchor, s.head), end: Math.max(s.anchor, s.head) + 1 };
}

export default function FullCalendarPage() {
  const [week] = useState(() => weekStart());
  const [blocks, setBlocks] = useState<Block[]>([]);
  const [cursor, setCursor] = useState({ day: 0, slot: 36 }); // 09:00 Monday
  const [kbSel, setKbSel] = useState<Sel | null>(null);
  const [kbActive, setKbActive] = useState(false);
  const [editing, setEditing] = useState<Block | null>(null);
  const [msg, announce] = useAnnouncer();
  const calRef = useRef<FullCalendar>(null);
  // FullCalendar's select()/unselect() APIs fire the same public callbacks as
  // user gestures do; this flag distinguishes our programmatic calls.
  const suppress = useRef(false);

  const api = () => calRef.current?.getApi();

  // Keyboard cursor/selection is visualized by abusing FullCalendar's own
  // selection highlight via calendar.select(). There is no real DOM focus on
  // the highlighted slot — focus stays on the wrapper div.
  useEffect(() => {
    const a = api();
    if (!a) return;
    if (!kbActive) return;
    const day = kbSel ? kbSel.day : cursor.day;
    const range = kbSel ? selRange(kbSel) : { start: cursor.slot, end: cursor.slot + 1 };
    suppress.current = true;
    a.select(slotToDate(week, day, range.start), slotToDate(week, day, range.end));
    suppress.current = false;
  }, [cursor, kbSel, kbActive, week]);

  const createBlock = (day: number, start: number, end: number) => {
    setBlocks((bs) => [...bs, { id: newId(), day, startSlot: start, endSlot: end }]);
    announce(`Availability block created, ${rangeLabel(day, start, end)}`);
  };

  const onSelect = (info: DateSelectArg) => {
    if (suppress.current) return; // our own cursor visualization, not a user gesture
    const s = dateToDaySlot(info.start);
    const e = dateToDaySlot(info.end);
    const endSlot = e.day !== s.day || e.slot === 0 ? SLOTS_PER_DAY : e.slot; // selection ending at midnight
    createBlock(s.day, s.slot, endSlot);
    const a = api();
    if (a) {
      suppress.current = true;
      a.unselect();
      suppress.current = false;
    }
  };

  const onUnselect = () => {
    if (suppress.current) return;
    setKbSel(null); // user clicked/dragged elsewhere; drop the keyboard selection
  };

  const onEventClick = (info: EventClickArg) => {
    const b = blocks.find((x) => x.id === info.event.id);
    if (b) setEditing(b);
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.target !== e.currentTarget) return; // let focused event elements handle their own keys
    const { day, slot } = cursor;

    const move = (dd: number, ds: number) => {
      e.preventDefault();
      if (e.shiftKey) {
        if (dd !== 0) return; // single-day selection
        const base = kbSel ?? { day, anchor: slot, head: slot };
        const next = { ...base, head: clamp(base.head + ds, 0, SLOTS_PER_DAY - 1) };
        setKbSel(next);
        const { start, end } = selRange(next);
        announce(`Selecting ${rangeLabel(next.day, start, end)}`);
      } else {
        setKbSel(null);
        const next = { day: clamp(day + dd, 0, 6), slot: clamp(slot + ds, 0, SLOTS_PER_DAY - 1) };
        setCursor(next);
        announce(rangeLabel(next.day, next.slot, next.slot + 1));
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
        if (kbSel) {
          e.preventDefault();
          const { start, end } = selRange(kbSel);
          createBlock(kbSel.day, start, end);
          setCursor({ day: kbSel.day, slot: kbSel.head });
          setKbSel(null);
        }
        break;
      case "Escape":
        if (kbSel) {
          setKbSel(null);
          announce("Selection cancelled");
        }
        break;
    }
  };

  const events = blocks.map((b) => ({
    id: b.id,
    title: "Available",
    start: slotToDate(week, b.day, b.startSlot),
    end: slotToDate(week, b.day, b.endSlot),
  }));

  return (
    <div className="page">
      <h1>FullCalendar</h1>
      <p className="hint">
        Pointer: drag on the grid (touch: long-press then drag). Keyboard: focus the calendar
        wrapper, arrows move the cursor, Shift+Up/Down selects, Enter creates, Escape cancels. Tab
        reaches existing blocks; Enter on a block opens edit/delete.
      </p>
      <LiveRegion message={msg} />
      <div
        className="fc-kb-wrap"
        role="application"
        aria-label="Availability calendar. Arrow keys move the cursor, Shift plus arrow keys select a range, Enter creates a block, Escape cancels."
        tabIndex={0}
        onKeyDown={onKeyDown}
        onFocus={(e) => {
          if (e.target === e.currentTarget) setKbActive(true);
        }}
        onBlur={(e) => {
          if (e.currentTarget.contains(e.relatedTarget as Node)) return;
          setKbActive(false);
          const a = api();
          if (a) {
            suppress.current = true;
            a.unselect();
            suppress.current = false;
          }
        }}
      >
        <FullCalendar
          ref={calRef}
          plugins={[timeGridPlugin, interactionPlugin]}
          initialView="timeGridWeek"
          initialDate={week}
          firstDay={1}
          headerToolbar={false}
          dayHeaderFormat={{ weekday: "short" }}
          allDaySlot={false}
          slotDuration="00:15:00"
          slotLabelInterval="01:00"
          scrollTime="09:00:00"
          height="auto"
          selectable
          selectMirror
          selectLongPressDelay={150}
          selectAllow={(span: DateSpanApi) =>
            span.start.getDay() === new Date(span.end.getTime() - 1).getDay()
          }
          select={onSelect}
          unselect={onUnselect}
          events={events}
          eventInteractive
          eventClick={onEventClick}
        />
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
        }}
        onDelete={() => {
          if (!editing) return;
          const b = editing;
          setEditing(null);
          setBlocks((bs) => bs.filter((x) => x.id !== b.id));
          announce(`Availability block deleted, ${rangeLabel(b.day, b.startSlot, b.endSlot)}`);
        }}
        onClose={() => setEditing(null)}
      />
    </div>
  );
}
