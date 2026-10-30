/** Types and pure helpers for the connectors screen (kept apart from the page so they can be unit-tested). */

export type System = "Moodle" | "Canvas" | "Sakai" | "Banner" | "Csv";
export const SYSTEMS: System[] = ["Moodle", "Canvas", "Sakai", "Banner", "Csv"];

export type Connector = {
  id: string; system: System; name: string; baseUrl: string | null; hasCredential: boolean; options: Record<string, string>;
  isActive: boolean; lastSyncAt: string | null; lastSyncStatus: string | null; lastSyncMessage: string | null; courses: number;
};
export type Course = {
  id: string; connectorId: string; externalId: string; code: string; title: string; term: string | null;
  startsOn: string | null; endsOn: string | null; enrolledCount: number | null; instructorNames: string; isActive: boolean; syncedAt: string;
};
export type CoursePage = { total: number; items: Course[] };

export type OptionHint = { key: string; en: string; ar: string; placeholder?: string };
export type SystemHelp = {
  /** Shown above the credential box: what to paste there. */
  credential: { en: string; ar: string };
  options: OptionHint[];
};

/** What each system needs, in plain words. Csv needs no address and no credential. */
export const HELP: Record<System, SystemHelp | null> = {
  Moodle: { credential: { en: "Web-service token (allowed to call core_course_get_courses_by_field)", ar: "رمز خدمة الويب (يسمح باستدعاء core_course_get_courses_by_field)" }, options: [] },
  Canvas: { credential: { en: "API access token", ar: "رمز الوصول إلى الواجهة البرمجية" },
    options: [{ key: "accountId", en: "Account id (default: self)", ar: "معرّف الحساب (الافتراضي: self)", placeholder: "1" }] },
  Sakai: { credential: { en: "Service account as username:password", ar: "حساب خدمة بصيغة اسم المستخدم:كلمة المرور" },
    options: [{ key: "siteType", en: "Site type (default: course)", ar: "نوع الموقع (الافتراضي: course)" }, { key: "sessionCookie", en: "Session cookie name (default: JSESSIONID)", ar: "اسم ملف الجلسة (الافتراضي: JSESSIONID)" }] },
  Banner: { credential: { en: "Ethos API key", ar: "مفتاح Ethos" },
    options: [{ key: "ethosAuthUrl", en: "Auth address (default: <address>/auth)", ar: "عنوان المصادقة (الافتراضي: ‎<العنوان>/auth)" }, { key: "acceptVersion", en: "Accept version (default: v16)", ar: "نسخة الواجهة (الافتراضي: v16)" }] },
  Csv: null,
};

export const isLive = (s: System) => s !== "Csv";

/** Only the options that were filled in, trimmed, and only those the system accepts. */
export function optionsFor(system: System, values: Record<string, string>): Record<string, string> {
  const allowed = new Set((HELP[system]?.options ?? []).map((o) => o.key));
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(values)) if (allowed.has(k) && v.trim()) out[k] = v.trim();
  return out;
}

/** Whether the form can be sent: a live system needs an address and a credential; a file needs only a name. */
export const canCreate = (system: System, name: string, baseUrl: string, credential: string) =>
  name.trim().length > 0 && name.trim().length <= 100 && (!isLive(system) || (baseUrl.trim().length > 0 && credential.length > 0));

/** The server's own words for a failed call, when it sent any (e.g. "the server refused the credential (401)"). */
export function errorText(body: unknown, fallback: string): string {
  if (body && typeof body === "object") {
    const o = body as { error?: unknown; problems?: unknown; more?: unknown };
    if (typeof o.error === "string") {
      const problems = Array.isArray(o.problems) ? o.problems.filter((p): p is string => typeof p === "string") : [];
      const more = typeof o.more === "number" && o.more > 0 ? ` (and ${o.more} more)` : "";
      return problems.length ? `${o.error} ${problems.join(" ")}${more}` : o.error;
    }
  }
  return fallback;
}

export const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;
