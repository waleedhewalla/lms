"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { config } from "../lib/config";
import { signOut } from "../lib/oidc";
import { BrandMark, LandingIcon } from "./LandingIcon";
import { PUBLIC_PATHS, PublicFooter, PublicHeader, useSignedInName } from "./PublicChrome";
import { useTranslation } from "./TranslationProvider";

export function AppLayout({ children }: { children: React.ReactNode }) {
  const { locale, setLocale, t } = useTranslation();
  const L = (en: string, ar: string) => (locale === "ar" ? ar : en);
  const [grafanaUrl, setGrafanaUrl] = useState("");
  useEffect(() => setGrafanaUrl(config().grafanaUrl), []);
  const pathname = usePathname();
  const user = useSignedInName();

  // Public pages (home, sign-in, privacy notice) get the marketing header and footer, not the workspace sidebar.
  if (PUBLIC_PATHS.includes(pathname)) {
    return (
      <div className="pub-page">
        <PublicHeader />
        <main id="main" className={pathname === "/" ? undefined : "pub-main"}>{children}</main>
        <PublicFooter />
      </div>
    );
  }

  return (
    <div className="app-container">
      <aside className="sidebar">
        <Link href="/" className="sidebar-brand" aria-label={L("EduNexus home", "EduNexus — الصفحة الرئيسية")}>
          <BrandMark size={30} /><h2>{t("app.title")}</h2>
        </Link>
        <nav style={{ flex: 1 }}>
          <Link href="/dashboard" className="nav-link">{t("nav.dashboard")}</Link>
          <Link href="/directory" className="nav-link">{t("nav.directory")}</Link>
          <Link href="/roles" className="nav-link">{t("nav.roles")}</Link>
          <Link href="/audit" className="nav-link">{t("nav.audit")}</Link>
          <Link href="/correspondence" className="nav-link">{t("nav.correspondence")}</Link>
          <Link href="/approvals" className="nav-link">{t("nav.approvals")}</Link>
          <Link href="/meetings" className="nav-link">{t("nav.meetings")}</Link>
          <Link href="/intelligence" className="nav-link">{t("nav.intelligence")}</Link>
          <hr style={{ border: 0, borderTop: "1px solid rgba(148,163,184,0.25)", margin: "0.75rem 0" }} />
          <Link href="/my-work" className="nav-link">📥 {L("My Work", "عملي")}</Link>
          <Link href="/requests" className="nav-link">📝 {L("Requests", "الطلبات")}</Link>
          <Link href="/tasks" className="nav-link">☑️ {L("Tasks", "المهام")}</Link>
          <Link href="/governance" className="nav-link">🏛️ {L("Committees & Minutes", "اللجان والمحاضر")}</Link>
          <Link href="/board" className="nav-link">🗳️ {L("Board", "مجلس الإدارة")}</Link>
          <Link href="/decisions" className="nav-link">⚖️ {L("Decisions", "القرارات")}</Link>
          <Link href="/policies" className="nav-link">📘 {L("Policies", "السياسات")}</Link>
          <Link href="/calendar" className="nav-link">🗓️ {L("Calendar", "التقويم")}</Link>
          <Link href="/reports" className="nav-link">📈 {L("Reports", "التقارير")}</Link>
          <Link href="/accreditation" className="nav-link">🎓 {L("Accreditation", "الاعتماد")}</Link>
          <Link href="/strategy" className="nav-link">🎯 {L("Strategy", "الاستراتيجية")}</Link>
          <Link href="/records" className="nav-link">🗄️ {L("Records Retention", "حفظ السجلات")}</Link>
          <Link href="/settings" className="nav-link">⚙️ {L("Settings", "الإعدادات")}</Link>
        </nav>
        <div style={{ marginTop: 'auto', opacity: 0.7, fontSize: '0.8rem' }}>
          <Link href="/privacy" style={{ color: "inherit" }}>{L("Privacy notice", "إشعار الخصوصية")}</Link>
          <p>{t("app.title")}</p>
        </div>
      </aside>
      
      <div className="main-content">
        <header className="header">
          <div style={{ flex: 1 }}></div>
          <div style={{ display: 'flex', gap: '1rem', alignItems: 'center' }}>
            <button className="btn btn-secondary" onClick={() => setLocale(locale === "ar" ? "en" : "ar")} style={{ padding: '0.4rem 1rem', fontSize: '0.875rem' }}>
              {locale === "ar" ? "English (LTR)" : "عربي (RTL)"}
            </button>
            {grafanaUrl && (
              <a href={grafanaUrl} target="_blank" rel="noreferrer" className="btn btn-secondary" style={{ padding: '0.4rem 1rem', fontSize: '0.875rem' }}>
                {t("btn.grafana")}
              </a>)}
            {user ? (
              <span className="account-chip">
                <span className="account-icon" aria-hidden="true"><LandingIcon name="user" size={18} /></span>
                <span className="account-name">{user}</span>
                <button type="button" className="btn btn-secondary" style={{ padding: '0.4rem 0.8rem', fontSize: '0.8rem' }} onClick={() => signOut()}>
                  {L("Sign out", "تسجيل الخروج")}</button>
              </span>
            ) : user === null && (
              <Link href={`/signin?next=${encodeURIComponent(pathname)}`} className="account-chip account-signin" title={L("Sign in", "تسجيل الدخول")}>
                <span className="account-icon" aria-hidden="true"><LandingIcon name="signin" size={18} /></span>
                <span>{L("Sign in", "تسجيل الدخول")}</span>
              </Link>)}
          </div>
        </header>
        
        <main className="page-container">
          {children}
        </main>
      </div>
    </div>
  );
}
