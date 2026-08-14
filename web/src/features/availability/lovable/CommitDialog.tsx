import { useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "./utils";
import { Button } from "./ui/button";
import { fmtRange, type WeekLabels } from "./partyup-data";
import type { Draft } from "./WeekGrid";

type Props = {
  draft: Draft | null;
  labels: WeekLabels;
  isMobile: boolean;
  pending?: boolean;
  onSave: (repeats: boolean) => void;
  onCancel: () => void;
};

export function CommitDialog({
  draft,
  labels,
  isMobile,
  pending = false,
  onSave,
  onCancel,
}: Props) {
  const { t } = useTranslation("availability");
  const [repeats, setRepeats] = useState(false);
  const titleId = useId();
  const firstControl = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  const draftKey = draft ? `${draft.day}:${draft.start}:${draft.end}` : null;
  const [seenKey, setSeenKey] = useState(draftKey);
  if (draftKey !== seenKey) {
    setSeenKey(draftKey);
    setRepeats(false);
  }

  useEffect(() => {
    if (draftKey === null) return;
    // The dialog conditional-mounts per selection, so nothing else moves focus
    // into it; without this the commit step is unreachable by keyboard.
    firstControl.current?.focus();
  }, [draftKey]);

  if (!draft) return null;

  const body = (
    <>
      <div className="min-w-0">
        <p className="font-display text-lg leading-tight font-extrabold tabular-nums" id={titleId}>
          {fmtRange(draft.start, draft.end)}
        </p>
        <p className="text-[12px] text-muted-foreground">
          {labels.dayLongNames[draft.day]} · {labels.dayDates[draft.day]}
        </p>
      </div>

      <div className="mt-3 grid grid-cols-2 gap-1.5 rounded-xl bg-muted p-1">
        {[
          { label: t("summary.kindOneOff"), val: false },
          { label: t("summary.weeklyHeading"), val: true },
        ].map((o, index) => (
          <button
            key={o.label}
            ref={index === 0 ? firstControl : undefined}
            aria-pressed={repeats === o.val}
            onClick={() => setRepeats(o.val)}
            className={cn(
              "h-11 rounded-lg font-display text-[13px] font-bold transition-colors",
              repeats === o.val
                ? "bg-card text-foreground shadow-[var(--shadow-card)]"
                : "text-muted-foreground",
            )}
          >
            {o.label}
          </button>
        ))}
      </div>

      <div className="mt-3 flex gap-2">
        <Button variant="outline" className="h-11 flex-1 rounded-xl" onClick={onCancel}>
          {t("commit.cancel")}
        </Button>
        <Button
          disabled={pending}
          className="h-11 flex-1 rounded-xl bg-primary font-display font-extrabold text-primary-foreground hover:bg-primary/90"
          onClick={() => onSave(repeats)}
        >
          {t("commit.save")}
        </Button>
      </div>
    </>
  );

  const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    // `aria-modal` promises Tab cannot leave, and nothing else here traps it.
    if (event.key !== "Tab") return;
    const focusable = [
      ...(panel.current?.querySelectorAll<HTMLElement>("button:not([disabled])") ?? []),
    ];
    if (focusable.length === 0) return;
    const first = focusable[0]!;
    const last = focusable[focusable.length - 1]!;
    const active = document.activeElement;
    if (event.shiftKey && (active === first || !panel.current?.contains(active))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || !panel.current?.contains(active))) {
      event.preventDefault();
      first.focus();
    }
  };

  if (isMobile) {
    return (
      <div
        className="fixed inset-0 z-50"
        role="dialog"
        aria-modal
        aria-labelledby={titleId}
        onKeyDown={onKeyDown}
      >
        <div className="absolute inset-0 bg-foreground/25" onClick={onCancel} />
        <div
          className="absolute inset-x-0 bottom-0 rounded-t-3xl border-t border-border bg-card p-4 pb-7 shadow-[var(--shadow-pop)]"
          ref={panel}
        >
          <div className="mx-auto mb-3 h-1.5 w-10 rounded-full bg-border" />
          {body}
        </div>
      </div>
    );
  }

  const top = Math.min(Math.max(draft.y - 40, 80), (typeof window !== "undefined" ? window.innerHeight : 800) - 260);
  const left = Math.min(
    Math.max(draft.x - 130, 16),
    (typeof window !== "undefined" ? window.innerWidth : 1200) - 292,
  );

  return (
    <div
      className="fixed inset-0 z-50"
      role="dialog"
      aria-modal
      aria-labelledby={titleId}
      onKeyDown={onKeyDown}
    >
      <div className="absolute inset-0" onClick={onCancel} />
      <div
        className="absolute w-[276px] rounded-2xl border border-border bg-card p-3.5 shadow-[var(--shadow-pop)]"
        ref={panel}
        style={{ top, left }}
      >
        {body}
      </div>
    </div>
  );
}
