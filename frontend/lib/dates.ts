// Dual-calendar dates: Gregorian and the official Saudi Hijri calendar (Umm al-Qura), via the browser's
// built-in Intl/ICU support. Display mode is a per-viewer preference stored in localStorage.

export type CalendarMode = "gregorian" | "hijri" | "both";
export type UiLocale = "en" | "ar";

const STORAGE_KEY = "edunexus.calendar";
export const CALENDAR_MODES: CalendarMode[] = ["gregorian", "hijri", "both"];

/** Arabic users default to both calendars (official correspondence is dated in Hijri); English to Gregorian. */
export const defaultMode = (locale: UiLocale): CalendarMode => (locale === "ar" ? "both" : "gregorian");

export function getCalendarMode(locale: UiLocale): CalendarMode {
  try {
    const saved = window.localStorage.getItem(STORAGE_KEY) as CalendarMode | null;
    if (saved && CALENDAR_MODES.includes(saved)) return saved;
  } catch { /* storage unavailable (private mode, SSR) */ }
  return defaultMode(locale);
}

export function setCalendarMode(mode: CalendarMode) {
  try { window.localStorage.setItem(STORAGE_KEY, mode); } catch { /* storage unavailable */ }
}

// The UI language, kept in step with TranslationProvider (it sets this while rendering, before children),
// so plain formatting helpers can follow the current language without each call site passing it.
let uiLocale: UiLocale = "en";
export const setUiLocale = (locale: UiLocale) => { uiLocale = locale; };
export const currentUiLocale = () => uiLocale;

type Options = { mode: CalendarMode; locale: UiLocale; time?: boolean; timeZone?: string };

const tag = (locale: UiLocale, calendar: "gregory" | "islamic-umalqura") =>
  `${locale === "ar" ? "ar-SA" : "en-US"}-u-ca-${calendar}`;

/** Formats an instant in the chosen calendar(s). Hijri never shows the time twice in "both" mode. */
export function formatDate(value: Date | string | null | undefined, { mode, locale, time = true, timeZone }: Options): string {
  if (value == null || value === "") return "–";
  const d = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(d.getTime())) return "–";
  const greg = new Intl.DateTimeFormat(tag(locale, "gregory"), {
    dateStyle: "medium", ...(time ? { timeStyle: "short" } : {}), timeZone,
  }).format(d);
  const hijriDate = new Intl.DateTimeFormat(tag(locale, "islamic-umalqura"), { dateStyle: "medium", timeZone }).format(d);
  if (mode === "gregorian") return greg;
  if (mode === "hijri") {
    if (!time) return hijriDate;
    const t = new Intl.DateTimeFormat(tag(locale, "gregory"), { timeStyle: "short", timeZone }).format(d);
    return `${hijriDate} ${t}`;
  }
  return `${greg} · ${hijriDate}`;
}

/** Hijri equivalent of a date/datetime-local input value ("2026-10-25" or "2026-10-25T09:00"). */
export function hijriForInput(value: string, locale: UiLocale): string {
  if (!value) return "";
  // A bare date would parse as UTC midnight and can land on the previous day; anchor it at local noon.
  const d = new Date(value.length === 10 ? `${value}T12:00` : value);
  return Number.isNaN(d.getTime()) ? "" : new Intl.DateTimeFormat(tag(locale, "islamic-umalqura"), { dateStyle: "long" }).format(d);
}
