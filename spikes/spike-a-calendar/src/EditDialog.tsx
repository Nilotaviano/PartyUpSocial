import { useEffect, useRef, useState } from "react";
import { Block, DAYS, SLOTS_PER_DAY, slotToTime } from "./lib";

interface Props {
  block: Block | null; // null = closed
  onSave: (startSlot: number, endSlot: number) => void;
  onDelete: () => void;
  onClose: () => void;
}

/** Native <dialog> editor: start/end selects in 15-min steps + Save/Delete/Cancel. */
export function EditDialog({ block, onSave, onDelete, onClose }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const [start, setStart] = useState(0);
  const [end, setEnd] = useState(1);

  useEffect(() => {
    const dlg = ref.current;
    if (!dlg) return;
    if (block) {
      setStart(block.startSlot);
      setEnd(block.endSlot);
      if (!dlg.open) dlg.showModal();
    } else if (dlg.open) {
      dlg.close();
    }
  }, [block]);

  const startOptions = [];
  for (let s = 0; s < SLOTS_PER_DAY; s++) startOptions.push(s);
  const endOptions = [];
  for (let s = start + 1; s <= SLOTS_PER_DAY; s++) endOptions.push(s);

  return (
    <dialog ref={ref} className="edit-dialog" aria-label="Edit availability block" onClose={onClose}>
      {block && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            onSave(start, end);
          }}
        >
          <h2>Edit block — {DAYS[block.day]}</h2>
          <div className="dialog-fields">
            <label>
              Start time
              <select
                value={start}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  setStart(v);
                  if (end <= v) setEnd(v + 1);
                }}
              >
                {startOptions.map((s) => (
                  <option key={s} value={s}>
                    {slotToTime(s)}
                  </option>
                ))}
              </select>
            </label>
            <label>
              End time
              <select value={end} onChange={(e) => setEnd(Number(e.target.value))}>
                {endOptions.map((s) => (
                  <option key={s} value={s}>
                    {slotToTime(s)}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="dialog-actions">
            <button type="submit">Save</button>
            <button type="button" onClick={onDelete}>
              Delete
            </button>
            <button type="button" onClick={onClose}>
              Cancel
            </button>
          </div>
        </form>
      )}
    </dialog>
  );
}
