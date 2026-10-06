"use client";
import Link from "next/link";
import { useTranslation } from "./TranslationProvider";

export function AppLayout({ children }: { children: React.ReactNode }) {
  const { locale, setLocale, t } = useTranslation();

  return (
    <div className="app-container">
      <aside className="sidebar">
        <h2>{t("app.title")}</h2>
        <nav style={{ flex: 1 }}>
          <Link href="/" className="nav-link">{t("nav.dashboard")}</Link>
          <Link href="/directory" className="nav-link">{t("nav.directory")}</Link>
          <Link href="/roles" className="nav-link">{t("nav.roles")}</Link>
          <Link href="/audit" className="nav-link">{t("nav.audit")}</Link>
          <Link href="/correspondence" className="nav-link">{t("nav.correspondence")}</Link>
          <Link href="/approvals" className="nav-link">{t("nav.approvals")}</Link>
          <Link href="/meetings" className="nav-link">{t("nav.meetings")}</Link>
          <Link href="/intelligence" className="nav-link">{t("nav.intelligence")}</Link>
        </nav>
        <div style={{ marginTop: 'auto', opacity: 0.7, fontSize: '0.8rem' }}>
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
            <a href="http://localhost:3021" target="_blank" rel="noreferrer" className="btn btn-secondary" style={{ padding: '0.4rem 1rem', fontSize: '0.875rem' }}>
              {t("btn.grafana")}
            </a>
          </div>
        </header>
        
        <main className="page-container">
          {children}
        </main>
      </div>
    </div>
  );
}
