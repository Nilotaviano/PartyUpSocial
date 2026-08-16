import { useState } from "react";
import { useTranslation } from "react-i18next";
import { CalendarDays, Pencil, Repeat, Trash2, X } from "lucide-react";
import { cn } from "./utils";
import { Button } from "./ui/button";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "./ui/alert-dialog";
import { fmt, ruleLabel, type AvailabilityRule, type WeekLabels } from "./partyup-data";

type Props = {
  rules: AvailabilityRule[];
  labels: WeekLabels;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onUpdate: (id: string, start: number, end: number) => void;
  onDelete: (id: string) => void;
};

const TIMES = Array.from({ length: 97 }, (_, i) => i * 15);

export function AvailabilityPanel({
  rules,
  labels,
  selectedId,
  onSelect,
  onUpdate,
  onDelete,
}: Props) {
  const { t } = useTranslation("availability");
  const [editing, setEditing] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<AvailabilityRule | null>(null);

  const recurring = rules.filter((r) => r.kind === "recurring");
  const oneoff = rules.filter((r) => r.kind === "oneoff");

  const renderRule = (rule: AvailabilityRule) => {
    const isEditing = editing === rule.id;
    return (
      <li key={rule.id}>
        <div
          className={cn(
            "rounded-xl border bg-background/60 p-2.5 transition-colors",
            selectedId === rule.id ? "border-ring ring-2 ring-ring/40" : "border-border/70",
          )}
        >
          <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
            <button
              onClick={() => onSelect(selectedId === rule.id ? null : rule.id)}
              className="flex min-w-0 items-center gap-2 text-left"
            >
              <span
                className={cn(
                  "grid size-7 shrink-0 place-items-center rounded-lg",
                  rule.intent === "free"
                    ? "bg-free text-free-foreground"
                    : "bg-maybe text-maybe-foreground",
                )}
              >
                {rule.kind === "recurring" ? (
                  <Repeat className="size-3.5" />
                ) : (
                  <CalendarDays className="size-3.5" />
                )}
              </span>
              <span className="min-w-0">
                <span className="block truncate font-display text-[13px] font-bold">
                  {ruleLabel(rule, labels)}
                </span>
                <span className="block text-[11px] text-muted-foreground">
                  {rule.intent === "free" ? t("lovable.free") : t("lovable.maybe")} ·{" "}
                  {rule.kind === "recurring"
                    ? t("summary.kindRecurring")
                    : t("summary.kindOneOff")}
                </span>
              </span>
            </button>
            <div className="flex shrink-0 items-center gap-0.5">
              <Button
                size="icon"
                variant="ghost"
                className="size-9 rounded-lg"
                aria-label={t("lovable.editWindow")}
                onClick={() => setEditing(isEditing ? null : rule.id)}
              >
                {isEditing ? <X className="size-4" /> : <Pencil className="size-4" />}
              </Button>
              <Button
                size="icon"
                variant="ghost"
                className="size-9 rounded-lg text-destructive hover:text-destructive"
                aria-label={t("lovable.deleteWindow")}
                onClick={() => (rule.kind === "recurring" ? setConfirm(rule) : onDelete(rule.id))}
              >
                <Trash2 className="size-4" />
              </Button>
            </div>
          </div>

          {isEditing && (
            <div className="mt-2 flex items-center gap-2 border-t border-border/70 pt-2">
              <TimeSelect
                label={t("block.start")}
                value={rule.start}
                onChange={(v) => onUpdate(rule.id, Math.min(v, rule.end - 15), rule.end)}
              />
              <span className="text-muted-foreground">–</span>
              <TimeSelect
                label={t("block.end")}
                value={rule.end}
                onChange={(v) => onUpdate(rule.id, rule.start, Math.max(v, rule.start + 15))}
              />
            </div>
          )}
        </div>
      </li>
    );
  };

  return (
    <>
      <div className="surface paper-grain p-4">
        <h2 className="font-display text-lg font-extrabold">{t("lovable.myAvailability")}</h2>
        <p className="mt-0.5 text-[12px] text-muted-foreground">{t("lovable.panelHint")}</p>

        <p className="mt-4 mb-2 font-display text-[11px] font-bold tracking-[0.14em] text-muted-foreground uppercase">
          {t("summary.weeklyHeading")}
        </p>
        <ul className="space-y-2">{recurring.map(renderRule)}</ul>
        {recurring.length === 0 && (
          <p className="text-[12px] text-muted-foreground">{t("summary.weeklyEmpty")}</p>
        )}

        <p className="mt-4 mb-2 font-display text-[11px] font-bold tracking-[0.14em] text-muted-foreground uppercase">
          {t("summary.oneOffHeading")}
        </p>
        <ul className="space-y-2">{oneoff.map(renderRule)}</ul>
        {oneoff.length === 0 && (
          <p className="text-[12px] text-muted-foreground">{t("summary.oneOffEmpty")}</p>
        )}
      </div>

      <AlertDialog open={!!confirm} onOpenChange={(o) => !o && setConfirm(null)}>
        <AlertDialogContent className="rounded-2xl">
          <AlertDialogHeader>
            <AlertDialogTitle className="font-display">
              {t("lovable.deleteRuleTitle")}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {t("lovable.deleteRuleBody", {
                rule: confirm ? ruleLabel(confirm, labels) : "",
              })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel className="rounded-xl">{t("summary.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="rounded-xl bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => {
                if (confirm) onDelete(confirm.id);
                setConfirm(null);
              }}
            >
              {t("lovable.deleteRule")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

function TimeSelect({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (v: number) => void;
}) {
  return (
    <select
      aria-label={label}
      value={value}
      onChange={(e) => onChange(Number(e.target.value))}
      className="h-11 min-w-0 flex-1 rounded-xl border border-input bg-card px-2 font-display text-sm font-bold tabular-nums"
    >
      {TIMES.map((t) => (
        <option key={t} value={t}>
          {fmt(t)}
        </option>
      ))}
    </select>
  );
}
