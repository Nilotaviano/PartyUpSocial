import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  ChevronLeft,
  ChevronRight,
  Clock3,
  Users,
  Vote,
  Gamepad2,
  ChevronDown,
} from "lucide-react";
import { Link, useLocation } from "react-router";
import { isApiError } from "../../../api/client";
import { segmentItemClasses } from "../../../components/ui/classes";
import { useToast } from "../../../components/ui/toastStore";
import { AgendaView, type AgendaEntry } from "../AgendaView";
import { OccurrenceScopeDialog, type OccurrenceScope } from "../OccurrenceScopeDialog";
import { AvatarDisc } from "../../friends/AvatarDisc";
import { getViewerProfile, viewerProfileKey } from "../../sessions/sessionQueries";
import {
  availabilityKeys,
  createAvailabilityBlock,
  createAvailabilitySeries,
  deleteAvailabilityBlock,
  deleteAvailabilitySeries,
  editAvailabilityBlock,
  editAvailabilityOccurrence,
  getCalendarView,
  invalidateAvailabilityQueries,
} from "../availabilityQueries";
import { cn } from "./utils";
import { Button } from "./ui/button";
import { useIsMobile } from "./use-mobile";
import { WeekGrid, type Draft } from "./WeekGrid";
import { AvailabilityPanel } from "./AvailabilityPanel";
import { CommitDialog } from "./CommitDialog";
import { mapCalendar, type MappedCalendar, type PollGroup } from "./calendarData";
import {
  fmtRange,
  ruleLabel,
  type Friend,
  type GameSession,
  type WeekLabels,
} from "./partyup-data";
import {
  DAY_MS,
  fakeLocalToInstant,
  hhmmOf,
  instantToFakeLocal,
  isoDateOf,
  startOfLocaleWeek,
  weekStartsOnIso,
} from "./zone";

// The five §8 problem codes render their own `availability.json` message; every
// other code reads the shipped flat `errors` namespace with its `generic`
// fallback.
const AVAILABILITY_PROBLEM_CODES = new Set([
  "availability_not_found",
  "availability_validation",
  "invalid_range",
  "range_too_large",
  "occurrence_not_found",
]);

function isoDayOfWeek(fakeLocal: Date) {
  return ((fakeLocal.getUTCDay() + 6) % 7) + 1;
}

export function CalendarShell() {
  const { t, i18n } = useTranslation(["availability", "errors"]);
  const queryClient = useQueryClient();
  const showToast = useToast();
  const isMobile = useIsMobile();
  // D-104: week and agenda are two renderings of one `GET /calendar` response,
  // never two fetches — the route only chooses which one is painted.
  const agendaMode = useLocation().pathname.endsWith("/agenda");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [earlyHours, setEarlyHours] = useState(false);
  const [earlyHoursMessage, setEarlyHoursMessage] = useState("");
  const [weekOffset, setWeekOffset] = useState(0);
  // `null` = no explicit paging yet; the window is then derived from today.
  const [pageStart, setPageStart] = useState<number | null>(null);
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [now, setNow] = useState<Date | null>(null);
  // D-117/D-135: an exclusion set, not an inclusion list — an empty set means
  // every friend is shown, so the "all friends by default" behavior holds the
  // instant the async calendar response arrives. Plain component state only:
  // no `localStorage`, no URL, no query key, so a remount resets it for free.
  const [excludedFriendIds, setExcludedFriendIds] = useState<Set<string>>(new Set());
  const toggleFriend = useCallback((id: string) => {
    setExcludedFriendIds((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const profile = useQuery({
    queryKey: viewerProfileKey,
    queryFn: getViewerProfile,
  });
  const timeZone = profile.data?.timeZone ?? "UTC";

  // The week is anchored on today's date in the viewer's zone, not on the mount
  // instant: a tab left open across local midnight must roll over with it. The
  // anchor is the date string, so the minute tick below only re-derives the
  // week when the day actually changes.
  const [todayIso, setTodayIso] = useState<string | null>(null);
  const weekStart = useMemo(() => {
    const zonedNow = todayIso
      ? new Date(`${todayIso}T00:00:00.000Z`)
      : instantToFakeLocal(new Date().toISOString(), timeZone);
    const anchor = new Date(zonedNow.getTime() + weekOffset * 7 * DAY_MS);
    return startOfLocaleWeek(anchor, weekStartsOnIso(i18n.language));
  }, [todayIso, timeZone, weekOffset, i18n.language]);

  useEffect(() => {
    const tick = () => {
      const zoned = instantToFakeLocal(new Date().toISOString(), timeZone);
      setNow(zoned);
      setTodayIso((current) => {
        const next = isoDateOf(zoned);
        return current === next ? current : next;
      });
    };
    tick();
    const timer = setInterval(tick, 60000);
    return () => clearInterval(timer);
  }, [timeZone]);

  const from = fakeLocalToInstant(weekStart, timeZone);
  const to = fakeLocalToInstant(new Date(weekStart.getTime() + 7 * DAY_MS), timeZone);

  // `Friend.id` (`friend-${ordinal}`) is only stable within one `GET /calendar`
  // response: the ordinal is the owner's encounter order in that response, not
  // a durable identity, so a paged-in response can hand the same id to a
  // different person. The exclusion set can't outlive the fetched range it was
  // built against, so it is cleared here — one mechanism for every cause of a
  // range change, week paging and the minute-tick midnight rollover alike —
  // rather than at each action that might change `from`. This resets state
  // during render on a prop/derived-value change (React's documented pattern),
  // not in a `useEffect`, so it never cascades an extra render.
  const [rangeAtLastClear, setRangeAtLastClear] = useState(from);
  if (rangeAtLastClear !== from) {
    setRangeAtLastClear(from);
    setExcludedFriendIds(new Set());
  }

  const calendar = useQuery({
    queryKey: availabilityKeys.calendar(from, to),
    queryFn: () => getCalendarView(from, to),
    enabled: Boolean(profile.data),
  });

  const errorMessage = useCallback(
    (error: unknown): string => {
      if (!isApiError(error)) return t("generic", { ns: "errors" });
      if (AVAILABILITY_PROBLEM_CODES.has(error.code)) {
        return t(`errors.codes.${error.code}`, { ns: "availability" });
      }
      return t(error.code, {
        ns: "errors",
        defaultValue: t("generic", { ns: "errors" }),
      });
    },
    [t],
  );

  useEffect(() => {
    if (calendar.error) showToast({ message: errorMessage(calendar.error) });
  }, [calendar.error, errorMessage, showToast]);

  // `timeZone: 'UTC'` is not a bug: the week dates hold the profile-zone wall
  // clock on their UTC accessors (D-046), so formatting reads them back the
  // same way — never through the host's own zone.
  const labels: WeekLabels = useMemo(() => {
    const shortDay = new Intl.DateTimeFormat(i18n.language, {
      weekday: "short",
      timeZone: "UTC",
    });
    const longDay = new Intl.DateTimeFormat(i18n.language, {
      weekday: "long",
      timeZone: "UTC",
    });
    const date = new Intl.DateTimeFormat(i18n.language, {
      day: "numeric",
      month: "short",
      timeZone: "UTC",
    });
    const dates = Array.from({ length: 7 }, (_, i) => new Date(weekStart.getTime() + i * DAY_MS));
    return {
      dayNames: dates.map((d) => shortDay.format(d)),
      dayLongNames: dates.map((d) => longDay.format(d)),
      dayDates: dates.map((d) => date.format(d)),
    };
  }, [weekStart, i18n.language]);

  const weekRangeLabel = useMemo(() => {
    const first = weekStart;
    const last = new Date(weekStart.getTime() + 6 * DAY_MS);
    const dayMonth = new Intl.DateTimeFormat(i18n.language, {
      day: "numeric",
      month: "short",
      timeZone: "UTC",
    });
    return dayMonth.formatRange(first, last);
  }, [weekStart, i18n.language]);

  const voteLabel = useCallback(
    (vote: string | null) =>
      vote
        ? t("lovable.upNext.yourVote", {
            vote: t(`a11y.pollVote.${vote}`, { defaultValue: vote }),
          })
        : t("lovable.upNext.noVote"),
    [t],
  );

  const mapped: MappedCalendar = useMemo(
    () =>
      mapCalendar(calendar.data, weekStart, timeZone, {
        anonymousFriend: t("calendar.anonymousFriend"),
        voteLabel,
      }),
    [calendar.data, weekStart, timeZone, t, voteLabel],
  );
  const rules = mapped.rules;

  const invalidate = () => invalidateAvailabilityQueries(queryClient);

  const createBlock = useMutation({
    mutationFn: createAvailabilityBlock,
    onSuccess: invalidate,
  });
  const createSeries = useMutation({
    mutationFn: createAvailabilitySeries,
    onSuccess: invalidate,
  });
  const deleteBlock = useMutation({
    mutationFn: deleteAvailabilityBlock,
    onSuccess: invalidate,
  });
  const deleteSeries = useMutation({
    mutationFn: deleteAvailabilitySeries,
    onSuccess: invalidate,
  });
  const editBlock = useMutation({
    mutationFn: ({
      blockId,
      body,
    }: {
      blockId: string;
      body: { startTime: string; endTime: string };
    }) => editAvailabilityBlock(blockId, body),
    onSuccess: invalidate,
  });
  const editOccurrence = useMutation({
    mutationFn: ({
      seriesId,
      occurrenceDate,
      body,
    }: {
      seriesId: string;
      occurrenceDate: string;
      body: Parameters<typeof editAvailabilityOccurrence>[2];
    }) => editAvailabilityOccurrence(seriesId, occurrenceDate, body),
    onSuccess: invalidate,
  });

  const todayIndex = useMemo(() => {
    if (!now) return -1;
    const index = Math.floor((now.getTime() - weekStart.getTime()) / DAY_MS);
    return index >= 0 && index < 7 ? index : -1;
  }, [now, weekStart]);
  const nowMinutes = now && todayIndex >= 0 ? now.getUTCHours() * 60 + now.getUTCMinutes() : null;

  const defaultPageStart = Math.max(0, Math.min((todayIndex < 0 ? 0 : todayIndex) - 1, 4));
  const pageStartValue = Math.max(0, Math.min(pageStart ?? defaultPageStart, 4));

  const rangeStart = earlyHours ? 0 : 8 * 60;
  const rangeEnd = 24 * 60;

  const days = useMemo(
    () =>
      isMobile ? [pageStartValue, pageStartValue + 1, pageStartValue + 2] : [0, 1, 2, 3, 4, 5, 6],
    [isMobile, pageStartValue],
  );

  const fakeLocalAt = useCallback(
    (day: number, minutes: number) =>
      new Date(weekStart.getTime() + day * DAY_MS + minutes * 60_000),
    [weekStart],
  );

  // D-132: `CommitDialog` conditional-mounts per selection, so nothing restores
  // focus when it unmounts. Focus goes back to the grid's roving tab stop once
  // the unmount has painted.
  const gridContainerRef = useRef<HTMLDivElement>(null);
  const wasDialogOpen = useRef(false);
  useEffect(() => {
    const isOpen = draft !== null;
    const closedJustNow = wasDialogOpen.current && !isOpen;
    wasDialogOpen.current = isOpen;
    if (!closedJustNow) return;
    const raf = requestAnimationFrame(() => {
      gridContainerRef.current
        ?.querySelector<HTMLElement>('[role="gridcell"][tabindex="0"]')
        ?.focus();
    });
    return () => cancelAnimationFrame(raf);
  }, [draft]);

  async function commit(repeats: boolean) {
    if (!draft) return;
    // The dialog stays up for the write so `pending` can disable Save; it is
    // dismissed in `finally`, and only if it still holds this same draft.
    const pending = draft;
    const { day, start, end } = draft;
    const startLocal = fakeLocalAt(day, start);
    const endLocal = fakeLocalAt(day, end);
    const range = fmtRange(start, end);
    const dayName = labels.dayNames[day] ?? "";
    try {
      if (repeats) {
        const created = await createSeries.mutateAsync({
          dayOfWeek: isoDayOfWeek(startLocal),
          // "HH:mm" — the wire's LocalTimePattern is strict, no seconds.
          startTime: hhmmOf(startLocal),
          durationMinutes: Math.round((endLocal.getTime() - startLocal.getTime()) / 60_000),
          effectiveFrom: isoDateOf(startLocal),
          effectiveUntil: null,
        });
        showToast({
          message: t("lovable.toast.savedWeekly", { day: dayName, range }),
          action: {
            label: t("commit.undo"),
            onAction: () => deleteSeries.mutate(created.seriesId),
          },
        });
      } else {
        const created = await createBlock.mutateAsync({
          startTime: fakeLocalToInstant(startLocal, timeZone),
          endTime: fakeLocalToInstant(endLocal, timeZone),
        });
        showToast({
          message: t("lovable.toast.savedOnce", { day: dayName, range }),
          action: {
            label: t("commit.undo"),
            onAction: () => deleteBlock.mutate(created.blockId),
          },
        });
      }
    } catch (error) {
      showToast({ message: errorMessage(error) });
    } finally {
      setDraft((current) => (current === pending ? null : current));
    }
  }

  async function applyResize(ruleId: string, start: number, end: number, scope: OccurrenceScope) {
    const target = mapped.targets.get(ruleId);
    const rule = mapped.rules.find((r) => r.id === ruleId);
    if (!target || !rule) return;
    // A window crossing midnight is painted as one segment per day, so the
    // write is the target's own instants moved by how far this segment's edges
    // travelled — rebuilding it from the segment alone would collapse the whole
    // window onto the segment's day and drop its other half.
    const nextStartLocal = new Date(
      instantToFakeLocal(target.start, timeZone).getTime() + (start - rule.start) * 60_000,
    );
    const nextEndLocal = new Date(
      instantToFakeLocal(target.end, timeZone).getTime() + (end - rule.end) * 60_000,
    );
    const startTime = fakeLocalToInstant(nextStartLocal, timeZone);
    const endTime = fakeLocalToInstant(nextEndLocal, timeZone);
    const restore = { startTime: target.start, endTime: target.end };

    const apply = async (body: { startTime: string; endTime: string }) => {
      if (target.kind === "block") {
        await editBlock.mutateAsync({ blockId: target.blockId, body });
        return;
      }
      if (scope === "thisAndFuture") {
        // A this-and-future edit rewrites the series' own shape, so it carries
        // the new weekday, its local "HH:mm" start and a duration — not the
        // two instants the one-off arm sends (`EditOccurrenceRequest`'s two
        // arms never share a body shape, and `endTime` must stay absent here).
        const startLocal = instantToFakeLocal(body.startTime, timeZone);
        const endLocal = instantToFakeLocal(body.endTime, timeZone);
        await editOccurrence.mutateAsync({
          seriesId: target.seriesId,
          occurrenceDate: target.occurrenceDate,
          body: {
            scope,
            startTime: hhmmOf(startLocal),
            endTime: null,
            dayOfWeek: isoDayOfWeek(startLocal),
            durationMinutes: Math.round((endLocal.getTime() - startLocal.getTime()) / 60_000),
          },
        });
        return;
      }
      await editOccurrence.mutateAsync({
        seriesId: target.seriesId,
        occurrenceDate: target.occurrenceDate,
        body: {
          scope,
          startTime: body.startTime,
          endTime: body.endTime,
          dayOfWeek: null,
          durationMinutes: null,
        },
      });
    };

    try {
      await apply({ startTime, endTime });
      showToast({
        message: t("lovable.toast.updated", { range: fmtRange(start, end) }),
        action: {
          label: t("commit.undo"),
          onAction: () => {
            void apply(restore).catch((error) => showToast({ message: errorMessage(error) }));
          },
        },
      });
    } catch (error) {
      showToast({ message: errorMessage(error) });
    }
  }

  // D-134/D-143: an edit that lands on one occurrence of a weekly series must
  // ask which occurrences it applies to before anything is written, defaulting
  // to this one alone. A one-time block carries no series, so it never asks.
  const [pendingScope, setPendingScope] = useState<{
    ruleId: string;
    start: number;
    end: number;
  } | null>(null);
  const scopeTrigger = useRef<HTMLElement | null>(null);

  function handleResize(ruleId: string, start: number, end: number) {
    if (mapped.targets.get(ruleId)?.kind === "occurrence") {
      scopeTrigger.current = document.activeElement as HTMLElement | null;
      setPendingScope({ ruleId, start, end });
      return;
    }
    void applyResize(ruleId, start, end, "onlyThis");
  }

  async function handleDelete(ruleId: string) {
    const target = mapped.targets.get(ruleId);
    const rule = mapped.rules.find((r) => r.id === ruleId);
    if (!target || !rule) return;
    setSelectedId(null);
    const label = ruleLabel(rule, labels);
    try {
      if (target.kind === "block") {
        await deleteBlock.mutateAsync(target.blockId);
        showToast({
          message: t("lovable.toast.deleted", { rule: label }),
          action: {
            label: t("commit.undo"),
            onAction: () => {
              createBlock.mutate({
                startTime: target.start,
                endTime: target.end,
              });
            },
          },
        });
        return;
      }
      // The confirm copy says a weekly rule goes from every future week, so
      // the delete is the series itself, not this one occurrence.
      await deleteSeries.mutateAsync(target.seriesId);
      const series = target.series;
      showToast({
        message: t("lovable.toast.deleted", { rule: label }),
        action: series
          ? {
              label: t("commit.undo"),
              onAction: () => {
                createSeries.mutate({
                  dayOfWeek: series.dayOfWeek,
                  startTime: series.startTime,
                  durationMinutes: series.durationMinutes,
                  effectiveFrom: series.effectiveFrom,
                  effectiveUntil: series.effectiveUntil,
                });
              },
            }
          : undefined,
      });
    } catch (error) {
      showToast({ message: errorMessage(error) });
    }
  }

  function toggleEarlyHours() {
    const next = !earlyHours;
    setEarlyHours(next);
    setEarlyHoursMessage(next ? t("viewport.earlyHoursShown") : t("viewport.earlyHoursHidden"));
  }

  function goToToday() {
    setWeekOffset(0);
    setPageStart(null);
  }

  function goToWeek(delta: -1 | 1) {
    setWeekOffset((value) => value + delta);
    setPageStart(null);
  }

  function stepDays(delta: -1 | 1) {
    setPageStart(Math.max(0, Math.min(pageStartValue + delta, 4)));
  }

  const totalHours = rules.reduce((acc, r) => acc + ((r.end - r.start) / 60) * r.days.length, 0);

  const agendaEntries: AgendaEntry[] = useMemo(
    () => [
      ...rules.map((rule) => ({
        id: `availability-${rule.id}`,
        kind: "availability" as const,
        start: fakeLocalAt(rule.days[0] ?? 0, rule.start),
        end: fakeLocalAt(rule.days[0] ?? 0, rule.end),
        title: t("calendar.layers.myAvailability"),
      })),
      ...mapped.sessions.map((session) => ({
        id: `session-${session.id}`,
        kind: "session" as const,
        start: fakeLocalAt(session.day, session.start),
        end: fakeLocalAt(session.day, session.end),
        title: session.title,
      })),
      ...mapped.polls.map((option) => ({
        id: `poll-option-${option.id}`,
        kind: "pollOption" as const,
        start: fakeLocalAt(option.day, option.start),
        end: fakeLocalAt(option.day, option.end),
        title: option.pollTitle,
      })),
    ],
    [rules, mapped.sessions, mapped.polls, fakeLocalAt, t],
  );

  // `timeZone: 'UTC'` again reads the fake-local wall clock back off the UTC
  // accessors the whole calendar carries it on (D-046).
  const agendaDate = useMemo(
    () =>
      new Intl.DateTimeFormat(i18n.language, {
        weekday: "short",
        day: "numeric",
        month: "short",
        timeZone: "UTC",
      }),
    [i18n.language],
  );
  const agendaTime = useMemo(
    () =>
      new Intl.DateTimeFormat(i18n.language, {
        hour: "numeric",
        minute: "2-digit",
        timeZone: "UTC",
      }),
    [i18n.language],
  );

  const panel = (
    <AvailabilityPanel
      rules={rules}
      labels={labels}
      selectedId={selectedId}
      onSelect={setSelectedId}
      onUpdate={handleResize}
      onDelete={(id) => void handleDelete(id)}
    />
  );

  return (
    <>
      <div className="min-h-screen bg-background pb-24" data-testid="screen-calendar">
        <header className="mx-auto flex max-w-[1400px] flex-col gap-3 px-3 pt-5 pb-3 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between sm:px-6">
          <div className="min-w-0">
            <h1 className="truncate font-display text-xl font-extrabold sm:text-2xl">
              {t("lovable.weekOf", { range: weekRangeLabel })}
            </h1>
          </div>
          <div className="flex shrink-0 items-center gap-2 self-stretch sm:self-auto">
            <Button
              variant="outline"
              size="icon"
              aria-label={t("calendar.previousWeek")}
              className="size-11 rounded-xl"
              onClick={() => goToWeek(-1)}
            >
              <ChevronLeft className="size-4" />
            </Button>
            <Button
              variant="outline"
              className="h-11 rounded-xl font-display font-bold"
              onClick={goToToday}
            >
              {t("calendar.dayPager.today")}
            </Button>
            <Button
              variant="outline"
              size="icon"
              aria-label={t("calendar.nextWeek")}
              className="size-11 rounded-xl"
              onClick={() => goToWeek(1)}
            >
              <ChevronRight className="size-4" />
            </Button>
            {/* App chrome inside the ported header: the view switcher belongs
                to this app's navigation, so it wears the SegmentStrip skin
                but sits with the week controls, kept horizontal at all sizes. */}
            <nav aria-label={t("calendar.viewSwitcher")} className="ml-auto sm:ml-2">
              <div
                className="flex flex-row [&>*]:rounded-none [&>*+*]:border-l-0 [&>*:first-child]:rounded-l-control [&>*:last-child]:rounded-r-control"
                role="tablist"
              >
                {[
                  { to: "/calendar", label: t("calendar.week"), on: !agendaMode },
                  {
                    to: "/calendar/agenda",
                    label: t("calendar.agenda"),
                    on: agendaMode,
                  },
                ].map((tab) => (
                  <Link
                    key={tab.to}
                    aria-selected={tab.on}
                    className={segmentItemClasses(tab.on, "flex-none")}
                    role="tab"
                    to={tab.to}
                  >
                    {tab.label}
                  </Link>
                ))}
              </div>
            </nav>
          </div>
        </header>

        <main className="mx-auto grid max-w-[1400px] gap-4 px-3 sm:px-6 lg:grid-cols-[320px_minmax(0,1fr)]">
          {/* side panel — desktop */}
          <aside className="hidden lg:block">
            <div className="sticky top-4 space-y-4">
              {panel}
              <Legend />
              <FriendsCard
                friends={mapped.friends}
                excludedFriendIds={excludedFriendIds}
                onToggleFriend={toggleFriend}
                sessions={mapped.sessions}
                labels={labels}
                pollGroups={mapped.pollGroups}
              />
            </div>
          </aside>

          <section className="min-w-0">
            <div className="surface paper-grain mb-3 p-3">
              {/* mobile collapsible summary */}
              <div className="lg:hidden">
                <button
                  onClick={() => setSummaryOpen((o) => !o)}
                  aria-expanded={summaryOpen}
                  className="grid w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-2 text-left"
                >
                  <span className="min-w-0">
                    <span className="block font-display text-base font-extrabold">
                      {t("lovable.myAvailability")}
                    </span>
                    <span className="block truncate text-[12px] text-muted-foreground">
                      {t("lovable.summaryLine", {
                        windows: rules.length,
                        hours: Math.round(totalHours),
                      })}
                    </span>
                  </span>
                  <ChevronDown
                    className={cn(
                      "size-5 shrink-0 transition-transform",
                      summaryOpen && "rotate-180",
                    )}
                  />
                </button>
                {summaryOpen && (
                  <div className="mt-3 space-y-3">
                    {panel}
                    <Legend />
                    <FriendsCard
                      friends={mapped.friends}
                      excludedFriendIds={excludedFriendIds}
                      onToggleFriend={toggleFriend}
                      sessions={mapped.sessions}
                      labels={labels}
                      pollGroups={mapped.pollGroups}
                    />
                  </div>
                )}
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2 lg:mt-0">
                <Button
                  variant="outline"
                  className="h-11 rounded-xl text-[13px] font-semibold"
                  onClick={toggleEarlyHours}
                >
                  <Clock3 className="size-4" />
                  {earlyHours ? t("lovable.hideEarly") : t("lovable.showEarly")}
                </Button>
                <span className="hidden text-[12px] text-muted-foreground sm:inline">
                  {t("lovable.paintHint")}
                </span>
                <p
                  aria-live="polite"
                  className="sr-only"
                  data-testid="early-hours-status"
                  role="status"
                >
                  {earlyHoursMessage}
                </p>
              </div>

              {/* mobile day pager */}
              <div className="mt-3 lg:hidden" data-testid="day-pager">
                <div className="grid grid-cols-7 gap-1">
                  {labels.dayNames.map((d, i) => (
                    <button
                      key={`${d}-${i}`}
                      aria-pressed={days.includes(i)}
                      aria-current={i === todayIndex ? "date" : undefined}
                      onClick={() => setPageStart(Math.max(0, Math.min(i, 4)))}
                      className={cn(
                        "h-11 rounded-xl border font-display text-[12px] font-bold",
                        days.includes(i)
                          ? "border-primary bg-primary/20"
                          : "border-border bg-background text-muted-foreground",
                        i === todayIndex && "underline decoration-2 underline-offset-4",
                      )}
                    >
                      {d}
                    </button>
                  ))}
                </div>
                <div className="mt-1.5 flex items-center gap-1.5">
                  <Button
                    variant="outline"
                    size="icon"
                    aria-label={t("lovable.earlierDays")}
                    className="size-11 shrink-0 rounded-xl"
                    onClick={() => stepDays(-1)}
                  >
                    <ChevronLeft className="size-4" />
                  </Button>
                  <p className="min-w-0 flex-1 truncate text-center font-display text-[12px] font-bold text-muted-foreground">
                    {t("lovable.dayPagerRange", {
                      start: labels.dayNames[days[0]!],
                      end: labels.dayNames[days[days.length - 1]!],
                    })}
                  </p>
                  <Button
                    variant="outline"
                    size="icon"
                    aria-label={t("lovable.laterDays")}
                    className="size-11 shrink-0 rounded-xl"
                    onClick={() => stepDays(1)}
                  >
                    <ChevronRight className="size-4" />
                  </Button>
                </div>
              </div>
            </div>

            <div className="surface p-2 sm:p-3" ref={gridContainerRef}>
              {agendaMode ? (
                <AgendaView
                  dateLabelFormatter={(date) => agendaDate.format(date)}
                  entries={agendaEntries}
                  timeLabelFormatter={(date) => agendaTime.format(date)}
                />
              ) : (
                <WeekGrid
                  rules={rules}
                  merged={mapped.merged}
                  friends={mapped.friends.filter((f) => !excludedFriendIds.has(f.id))}
                  sessions={mapped.sessions}
                  polls={mapped.polls}
                  labels={labels}
                  days={days}
                  rangeStart={rangeStart}
                  rangeEnd={rangeEnd}
                  todayIndex={todayIndex}
                  nowMinutes={nowMinutes}
                  selectedId={selectedId}
                  onSelect={setSelectedId}
                  onDraftComplete={setDraft}
                  onResize={handleResize}
                  onPageStep={isMobile ? stepDays : undefined}
                />
              )}
            </div>

            <div className="mt-4 lg:hidden">
              <UpNext sessions={mapped.sessions} labels={labels} pollGroups={mapped.pollGroups} />
            </div>
          </section>
        </main>

        <OccurrenceScopeDialog
          open={pendingScope !== null}
          triggerRef={scopeTrigger}
          onConfirm={(scope) => {
            const target = pendingScope;
            setPendingScope(null);
            if (target) void applyResize(target.ruleId, target.start, target.end, scope);
          }}
          onOpenChange={(next) => {
            if (!next) setPendingScope(null);
          }}
        />

        <CommitDialog
          draft={draft}
          labels={labels}
          isMobile={isMobile}
          pending={createBlock.isPending || createSeries.isPending}
          onSave={(repeats) => void commit(repeats)}
          onCancel={() => setDraft(null)}
        />
      </div>
    </>
  );
}

function Legend() {
  const { t } = useTranslation("availability");
  const items = [
    {
      label: t("lovable.legend.free"),
      cls: "bg-free border-free-foreground/30",
      glyph: null,
    },
    {
      label: t("lovable.legend.maybe"),
      cls: "bg-maybe border-dashed border-maybe-foreground/50",
      glyph: null,
    },
    {
      label: t("lovable.legend.friend"),
      cls: "bg-card border-border rounded-full",
      glyph: "●",
    },
    {
      label: t("lovable.legend.session"),
      cls: "bg-accent border-foreground/70",
      glyph: "🎮",
    },
    {
      label: t("lovable.legend.poll"),
      cls: "bg-card border-dashed border-foreground/50",
      glyph: "?",
    },
  ];
  return (
    <div className="surface p-4">
      <h2 className="font-display text-base font-extrabold">{t("lovable.legend.heading")}</h2>
      <ul className="mt-2 space-y-1.5">
        {items.map((i) => (
          <li key={i.label} className="flex items-center gap-2 text-[12px]">
            <span
              aria-hidden="true"
              className={cn(
                "grid size-4 shrink-0 place-items-center rounded-[5px] border-2 text-[8px] leading-none font-bold",
                i.cls,
              )}
            >
              {i.glyph}
            </span>
            <span className="min-w-0 truncate font-semibold">{i.label}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function FriendsCard({
  friends,
  excludedFriendIds,
  onToggleFriend,
  sessions,
  pollGroups,
  labels,
}: {
  friends: Friend[];
  excludedFriendIds: Set<string>;
  onToggleFriend: (id: string) => void;
  sessions: GameSession[];
  pollGroups: PollGroup[];
  labels: WeekLabels;
}) {
  const { t } = useTranslation("availability");
  return (
    <div className="surface p-4">
      <h2 className="flex items-center gap-2 font-display text-base font-extrabold">
        <Users className="size-4" /> {t("lovable.crew")}
      </h2>
      <ul className="mt-2 flex flex-wrap gap-1.5">
        {friends.map((f) => {
          const shown = !excludedFriendIds.has(f.id);
          return (
            <li key={f.id}>
              <button
                type="button"
                aria-pressed={shown}
                aria-label={t("lovable.friendToggle", { name: f.name })}
                onClick={() => onToggleFriend(f.id)}
                className={cn(
                  "flex min-h-11 items-center gap-1.5 rounded-full border border-border bg-background py-0.5 pr-2.5 pl-0.5",
                  // WCAG 1.4.3: an aria-pressed="false" button is still operable, so
                  // the off state can't drop the label below 4.5:1 via opacity — the
                  // off signal is the dashed border plus a dimmed (aria-hidden) disc.
                  !shown && "border-dashed text-muted-foreground",
                )}
              >
                <AvatarDisc
                  seed={f.id}
                  emoji={f.emoji}
                  imgUrl={f.imgUrl}
                  masked={f.masked}
                  size="xs"
                  className={!shown ? "opacity-40" : undefined}
                />
                <span className="text-[12px] font-semibold">{f.name.split(" ")[0]}</span>
              </button>
            </li>
          );
        })}
      </ul>
      <UpNext compact sessions={sessions} pollGroups={pollGroups} labels={labels} />
    </div>
  );
}

function UpNext({
  compact = false,
  sessions,
  pollGroups,
  labels,
}: {
  compact?: boolean;
  sessions: GameSession[];
  pollGroups: PollGroup[];
  labels: WeekLabels;
}) {
  const { t } = useTranslation("availability");
  return (
    <div className={cn(!compact && "surface p-4", compact && "mt-4 border-t border-border pt-3")}>
      <h3 className="flex items-center gap-2 font-display text-[13px] font-extrabold">
        <Gamepad2 className="size-4" /> {t("lovable.bookedThisWeek")}
      </h3>
      <ul className="mt-2 space-y-1.5">
        {sessions.map((s) => (
          <li key={s.id} className="flex items-center gap-2 text-[12px]">
            <span className="size-2 shrink-0 rounded-full bg-accent" />
            <span className="min-w-0 truncate font-semibold">{s.title}</span>
            <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">
              {labels.dayNames[s.day]} {fmtRange(s.start, s.end)}
            </span>
          </li>
        ))}
      </ul>
      {pollGroups.map((group) => (
        <div key={group.pollId}>
          <h3 className="mt-3 flex items-center gap-2 font-display text-[13px] font-extrabold">
            <Vote className="size-4" /> {t("lovable.openPoll", { title: group.title })}
          </h3>
          <ul className="mt-2 space-y-1.5">
            {group.options.map((p) => (
              <li key={p.id} className="flex items-center gap-2 text-[12px]">
                <span className="size-2 shrink-0 rounded-full border border-dashed border-foreground" />
                <span className="min-w-0 truncate font-semibold">
                  {labels.dayNames[p.day]} {labels.dayDates[p.day]}
                </span>
                <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">
                  {fmtRange(p.start, p.end)} · {p.voteLabel}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}
