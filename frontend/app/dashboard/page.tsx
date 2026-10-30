"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { api } from "../../lib/api";
import { formatDate } from "../../lib/dates";
import { canOpen, GROUPS, MODULES } from "../../lib/modules";
import { LandingIcon } from "../../components/LandingIcon";
import { useSession } from "../../components/PublicChrome";
import { useTranslation } from "../../components/TranslationProvider";

type Counts = { approvals: number; tasks: number; overdue: number; unreadNotifications: number };

/** The signed-in home: every module this person can open, grouped, with what is waiting on them. */
export default function ModulesHome() {
  const { locale } = useTranslation();
  const L = (en: string, ar: string) => (locale === "ar" ? ar : en);
  const session = useSession();
  const [counts, setCounts] = useState<Counts | null>(null);
  const [today, setToday] = useState("");

  // Both calendars, whatever the display setting: the greeting is where people check today's Hijri date.
  useEffect(() => setToday(formatDate(new Date(), { mode: "both", locale, time: false })), [locale]);
  useEffect(() => {
    if (!session?.perms.includes("inbox:read")) return;
    let tenant = "";
    try { tenant = window.localStorage.getItem("edunexus.tenant") ?? ""; } catch { /* storage unavailable */ }
    // Counts need the account to be linked to a person; without that the badges simply stay hidden.
    if (tenant) api<Counts>(`/api/inbox/counts?tenantId=${tenant}`).then(setCounts).catch(() => setCounts(null));
  }, [session]);

  if (!session) return null;
  const visible = MODULES.filter((m) => canOpen(m, session.perms));
  const badge: Record<string, number | undefined> = {
    "/my-work": counts ? counts.approvals + counts.tasks : undefined,
    "/approvals": counts?.approvals,
    "/tasks": counts?.tasks,
  };
  const hour = new Date().getHours();
  const greeting = hour < 12 ? L("Good morning", "صباح الخير") : hour < 18 ? L("Good afternoon", "مساء الخير") : L("Good evening", "مساء الخير");

  return (
    <div className="ws-home">
      <section className="ws-hero">
        <div>
          <p className="ws-date"><LandingIcon name="calendar" size={16} />{today}</p>
          <h1>{greeting}, {session.name}</h1>
          <p className="ws-sub">
            {visible.length > 0
              ? L(`You have access to ${visible.length} modules. Choose one to start.`, `لديك صلاحية على ${visible.length} وحدة. اختر وحدة للبدء.`)
              : L("Your account has no modules yet. Ask your administrator to assign you a role.", "لا توجد وحدات لحسابك بعد. اطلب من مدير النظام إسناد دور لك.")}
          </p>
        </div>
        {counts && (
          <div className="ws-stats">
            <Link href="/approvals" className="ws-stat"><b>{counts.approvals}</b><span>{L("Approvals waiting", "موافقات بانتظارك")}</span></Link>
            <Link href="/tasks" className="ws-stat"><b>{counts.tasks}</b><span>{L("Open tasks", "مهام مفتوحة")}</span></Link>
            <Link href="/tasks" className={`ws-stat${counts.overdue ? " ws-stat-alert" : ""}`}><b>{counts.overdue}</b><span>{L("Overdue", "متأخرة")}</span></Link>
          </div>)}
      </section>

      {GROUPS.map((g) => {
        const mods = visible.filter((m) => m.group === g.key);
        if (mods.length === 0) return null;
        return (
          <section key={g.key} className="ws-group" aria-labelledby={`ws-${g.key}`}>
            <h2 id={`ws-${g.key}`}>{L(g.en, g.ar)}</h2>
            <div className="ws-grid">
              {mods.map((m) => {
                const [title, desc] = locale === "ar" ? m.ar : m.en;
                const n = badge[m.href];
                return (
                  <Link key={m.href} href={m.href} className="ws-module">
                    <span className={`pub-tile pub-tone-${m.tone}`}><LandingIcon name={m.icon} /></span>
                    <span className="ws-module-text"><b>{title}</b><small>{desc}</small></span>
                    {n ? <span className="ws-badge" aria-label={L(`${n} waiting`, `${n} بانتظارك`)}>{n}</span>
                       : <LandingIcon name="arrow" size={18} className="ws-go pub-flip" />}
                  </Link>);
              })}
            </div>
          </section>);
      })}
    </div>
  );
}
