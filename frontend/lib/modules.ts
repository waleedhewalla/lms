import type { LandingIconName } from "../components/LandingIcon";

/**
 * Every workspace module, in one place: the sidebar and the dashboard launcher are both drawn from this list,
 * and a module shows only for people who hold at least one of its permissions (an empty list means everyone).
 * The API still checks every call; hiding a module is for clarity, not security.
 */
export type ModuleGroup = "work" | "governance" | "quality" | "admin";

export type Module = {
  href: string;
  icon: LandingIconName;
  tone: "blue" | "green" | "amber" | "violet" | "teal" | "red";
  group: ModuleGroup;
  en: [title: string, description: string];
  ar: [title: string, description: string];
  perms: string[];
};

export const GROUPS: Array<{ key: ModuleGroup; en: string; ar: string }> = [
  { key: "work", en: "My work", ar: "عملي" },
  { key: "governance", en: "Governance", ar: "الحوكمة" },
  { key: "quality", en: "Quality & strategy", ar: "الجودة والاستراتيجية" },
  { key: "admin", en: "Administration", ar: "الإدارة" },
];

export const MODULES: Module[] = [
  { href: "/my-work", icon: "inbox", tone: "blue", group: "work", perms: ["inbox:read"],
    en: ["My Work", "Everything waiting on you, in one inbox."], ar: ["عملي", "كل ما ينتظرك في صندوق واحد."] },
  { href: "/requests", icon: "tasks", tone: "green", group: "work", perms: ["request:read", "request:create"],
    en: ["Requests", "Submit and track form-based requests."], ar: ["الطلبات", "قدّم الطلبات بالنماذج وتابعها."] },
  { href: "/tasks", icon: "check", tone: "amber", group: "work", perms: ["task:read"],
    en: ["Tasks", "Assigned work, evidence and verification."], ar: ["المهام", "الأعمال المسندة والشواهد والتحقق."] },
  { href: "/approvals", icon: "check", tone: "green", group: "work", perms: ["approval:read", "approval:decide"],
    en: ["Approvals", "Review and decide multi-stage approvals."], ar: ["الموافقات", "مراجعة الموافقات متعددة المراحل والبت فيها."] },
  { href: "/correspondence", icon: "mail", tone: "blue", group: "work", perms: ["correspondence:read"],
    en: ["Correspondence", "Incoming, outgoing and internal letters."], ar: ["المراسلات", "المراسلات الواردة والصادرة والداخلية."] },
  { href: "/calendar", icon: "calendar", tone: "violet", group: "work", perms: ["calendar:read"],
    en: ["Calendar", "Meetings, deadlines and reviews, with Hijri dates."], ar: ["التقويم", "الاجتماعات والمواعيد والمراجعات بالتاريخ الهجري."] },

  { href: "/governance", icon: "people", tone: "violet", group: "governance", perms: ["committee:read"],
    en: ["Committees & Minutes", "Committees, members, terms and approved minutes."], ar: ["اللجان والمحاضر", "اللجان والأعضاء والعضويات والمحاضر المعتمدة."] },
  { href: "/meetings", icon: "calendar", tone: "teal", group: "governance", perms: ["meeting:read"],
    en: ["Meetings", "Agenda, attendance, quorum and voting."], ar: ["الاجتماعات", "جدول الأعمال والحضور والنصاب والتصويت."] },
  { href: "/board", icon: "vote", tone: "teal", group: "governance", perms: ["meeting:read"],
    en: ["Board", "Written resolutions and the interest register."], ar: ["مجلس الإدارة", "القرارات بالتمرير وسجل الإفصاح عن المصالح."] },
  { href: "/decisions", icon: "scale", tone: "red", group: "governance", perms: ["decision:read"],
    en: ["Decisions", "Decisions followed to actions and evidence."], ar: ["القرارات", "متابعة القرارات حتى الإجراءات والشواهد."] },
  { href: "/policies", icon: "book", tone: "blue", group: "governance", perms: ["policy:read"],
    en: ["Policies", "Versions, review, quizzes and attestations."], ar: ["السياسات", "النسخ والمراجعة والاختبارات والإقرارات."] },

  { href: "/accreditation", icon: "award", tone: "green", group: "quality", perms: ["quality:read"],
    en: ["Accreditation", "Standards, evidence crosswalk and self-study."], ar: ["الاعتماد الأكاديمي", "المعايير ومصفوفة الشواهد والدراسة الذاتية."] },
  { href: "/strategy", icon: "target", tone: "amber", group: "quality", perms: ["strategy:read"],
    en: ["Strategy", "Plans, objectives and the KPI scorecard."], ar: ["الاستراتيجية", "الخطط والأهداف وبطاقة المؤشرات."] },
  { href: "/reports", icon: "chart", tone: "teal", group: "quality", perms: ["analytics:read"],
    en: ["Reports", "Dashboards and exports with Hijri dates."], ar: ["التقارير", "لوحات المتابعة والتصدير بالتاريخ الهجري."] },
  { href: "/intelligence", icon: "sparkle", tone: "red", group: "quality", perms: ["ai:read", "ai:ask", "search:read", "analytics:read"],
    en: ["Intelligence", "Search, AI assistants and institutional memory."], ar: ["الذكاء المؤسسي", "البحث والمساعدات الذكية والذاكرة المؤسسية."] },

  { href: "/directory", icon: "people", tone: "blue", group: "admin", perms: ["person:read"],
    en: ["Directory", "People, departments and structure."], ar: ["الدليل", "الأشخاص والأقسام والهيكل."] },
  { href: "/connectors", icon: "globe", tone: "teal", group: "admin", perms: ["integration:read", "integration:manage", "integration:admin", "integration:write"],
    en: ["Connected systems", "Course catalogue from your learning or student system."], ar: ["الأنظمة المرتبطة", "فهرس المقررات من نظام التعلم أو شؤون الطلاب."] },
  { href: "/roles", icon: "key", tone: "violet", group: "admin", perms: ["role:read", "role:assign"],
    en: ["Roles & Access", "Roles, assignments and delegations."], ar: ["الأدوار والصلاحيات", "الأدوار والإسناد والتفويض."] },
  { href: "/audit", icon: "history", tone: "amber", group: "admin", perms: ["audit:read"],
    en: ["Audit Trail", "Every action, who did it and when."], ar: ["سجل التدقيق", "كل إجراء ومن قام به ومتى."] },
  { href: "/records", icon: "archive", tone: "red", group: "admin", perms: ["records:read"],
    en: ["Records Retention", "Schedules, legal holds and disposal."], ar: ["حفظ السجلات", "جداول الحفظ والحجز القانوني والإتلاف."] },
  { href: "/settings", icon: "lock", tone: "green", group: "admin", perms: [],
    en: ["Settings", "Your preferences, data and privacy."], ar: ["الإعدادات", "تفضيلاتك وبياناتك وخصوصيتك."] },
];

/** The permissions in a decoded token's claims ("permission" may be one value or a list). */
export function permissionsOf(claims: Record<string, unknown> | null): string[] {
  const p = claims?.permission;
  return Array.isArray(p) ? p.map(String) : typeof p === "string" ? [p] : [];
}

/** The directory person this account acts as (the token's person_id claim), or null when the account is not linked to one. */
export function personIdOf(claims: Record<string, unknown> | null): string | null {
  const v = claims?.person_id;
  return typeof v === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v) ? v : null;
}

export const canOpen = (m: Module, perms: string[]) => m.perms.length === 0 || m.perms.some((p) => perms.includes(p));

/** The module a path belongs to (e.g. "/meetings/123" → Meetings), if any. */
export const moduleFor = (path: string) => MODULES.find((m) => path === m.href || path.startsWith(m.href + "/"));
