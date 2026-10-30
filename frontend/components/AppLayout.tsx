"use client";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { config } from "../lib/config";
import { canOpen, GROUPS, moduleFor, MODULES } from "../lib/modules";
import { signOut } from "../lib/oidc";
import { BrandMark, LandingIcon } from "./LandingIcon";
import { OPEN_PATHS, PUBLIC_PATHS, PublicFooter, PublicHeader, useSession } from "./PublicChrome";
import { useTranslation } from "./TranslationProvider";

export function AppLayout({ children }: { children: React.ReactNode }) {
  const { locale, setLocale, t } = useTranslation();
  const L = (en: string, ar: string) => (locale === "ar" ? ar : en);
  const [grafanaUrl, setGrafanaUrl] = useState("");
  useEffect(() => setGrafanaUrl(config().grafanaUrl), []);
  const pathname = usePathname();
  const router = useRouter();
  const session = useSession();
  const isPublic = PUBLIC_PATHS.includes(pathname);
  const isOpen = OPEN_PATHS.includes(pathname);

  // The workspace is for signed-in people only: anyone else goes to sign-in and comes back here afterwards.
  useEffect(() => {
    if (!isPublic && !isOpen && session === null) router.replace(`/signin?next=${encodeURIComponent(pathname)}`);
  }, [isPublic, isOpen, session, pathname, router]);

  // Public pages (home, sign-in, privacy notice) get the marketing header and footer, not the workspace sidebar.
  if (isPublic) {
    return (
      <div className="pub-page">
        <PublicHeader />
        <main id="main" className={pathname === "/" ? undefined : "pub-main"}>{children}</main>
        <PublicFooter />
      </div>
    );
  }
  if (isOpen) return <main className="page-container">{children}</main>;
  if (!session) {
    return <div className="ws-wait" role="status">{session === null ? L("Redirecting to sign in…", "جارٍ التحويل إلى تسجيل الدخول…") : L("Loading…", "جارٍ التحميل…")}</div>;
  }

  const current = moduleFor(pathname);
  const denied = current && !canOpen(current, session.perms) ? current : null;

  return (
    <div className="app-container">
      <aside className="sidebar">
        <Link href="/" className="sidebar-brand" aria-label={L("EduNexus home", "EduNexus — الصفحة الرئيسية")}>
          <BrandMark size={30} /><h2>{t("app.title")}</h2>
        </Link>
        <nav style={{ flex: 1 }} aria-label={L("Modules", "الوحدات")}>
          <Link href="/dashboard" className={`nav-link ws-nav${pathname === "/dashboard" ? " ws-nav-on" : ""}`}
            aria-current={pathname === "/dashboard" ? "page" : undefined}>
            <LandingIcon name="building" size={18} />{L("All modules", "كل الوحدات")}
          </Link>
          {GROUPS.map((g) => {
            const mods = MODULES.filter((m) => m.group === g.key && canOpen(m, session.perms));
            if (mods.length === 0) return null;
            return (
              <div key={g.key} className="ws-nav-group">
                <p className="ws-nav-title">{L(g.en, g.ar)}</p>
                {mods.map((m) => {
                  const on = current?.href === m.href;
                  return (
                    <Link key={m.href} href={m.href} className={`nav-link ws-nav${on ? " ws-nav-on" : ""}`} aria-current={on ? "page" : undefined}>
                      <LandingIcon name={m.icon} size={18} />{locale === "ar" ? m.ar[0] : m.en[0]}
                    </Link>);
                })}
              </div>);
          })}
        </nav>
        <div style={{ marginTop: 'auto', opacity: 0.7, fontSize: '0.8rem' }}>
          <Link href="/privacy" style={{ color: "inherit" }}>{L("Privacy notice", "إشعار الخصوصية")}</Link>
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
            <span className="account-chip">
              <span className="account-icon" aria-hidden="true"><LandingIcon name="user" size={18} /></span>
              <span className="account-name">{session.name}</span>
              <button type="button" className="btn btn-secondary" style={{ padding: '0.4rem 0.8rem', fontSize: '0.8rem' }} onClick={() => signOut()}>
                {L("Sign out", "تسجيل الخروج")}</button>
            </span>
          </div>
        </header>

        <main className="page-container">
          {!denied ? children : (
            <div className="glass-card ws-denied" role="alert">
              <span className="pub-tile pub-tone-red"><LandingIcon name="lock" /></span>
              <h1>{L("You don't have access to this module", "ليست لديك صلاحية لهذه الوحدة")}</h1>
              <p>{L(`${denied.en[0]} needs a role your account does not have. Ask your administrator if you need it.`,
                    `تتطلب وحدة «${denied.ar[0]}» دورًا غير متاح لحسابك. اطلب ذلك من مدير النظام إذا احتجت إليه.`)}</p>
              <Link href="/dashboard" className="btn">{L("Back to my modules", "العودة إلى وحداتي")}</Link>
            </div>)}
        </main>
      </div>
    </div>
  );
}
