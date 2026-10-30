"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { claims } from "../lib/oidc";
import { permissionsOf, personIdOf } from "../lib/modules";
import { BrandMark, LandingIcon } from "./LandingIcon";
import { useTranslation } from "./TranslationProvider";

/** Pages a signed-out visitor can read: drawn with the public header and footer instead of the workspace sidebar. */
export const PUBLIC_PATHS = ["/", "/signin", "/privacy"];

/** Pages that must work without a session but are not part of the public site (the sign-in redirect lands here). */
export const OPEN_PATHS = ["/auth/callback"];

const NAV = [
  { href: "/#features", en: "Features", ar: "المزايا" },
  { href: "/#how-it-works", en: "How it works", ar: "كيف تعمل" },
  { href: "/#roles", en: "For your role", ar: "لكل دور" },
  { href: "/#trust", en: "Trust & compliance", ar: "الثقة والامتثال" },
];

export type Session = { name: string; perms: string[]; personId: string | null };

/** The signed-in session from the stored, unexpired token: undefined while loading, null when signed out. */
export function useSession(): Session | null | undefined {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  useEffect(() => {
    const c = claims();
    setSession(c ? { name: String(c.name ?? c.preferred_username ?? c.email ?? c.sub ?? "") || "—", perms: permissionsOf(c), personId: personIdOf(c) } : null);
  }, []);
  return session;
}

/** Who is signed in, or null. */
export function useSignedInName(): string | null | undefined {
  const s = useSession();
  return s === undefined ? undefined : s?.name ?? null;
}

function useL() {
  const { locale } = useTranslation();
  return (en: string, ar: string) => (locale === "ar" ? ar : en);
}

export function PublicHeader() {
  const L = useL();
  const { locale, setLocale } = useTranslation();
  const onHome = usePathname() === "/";
  const [scrolled, setScrolled] = useState(false);
  const [open, setOpen] = useState(false);
  const user = useSignedInName();

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 24);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  // Over the hero the bar is transparent; it turns solid once the visitor scrolls, or on any other public page.
  const solid = scrolled || !onHome || open;
  const signInLabel = user ? L("Open my workspace", "افتح مساحة عملي") : L("Sign in", "تسجيل الدخول");

  return (
    <header className={`pub-header${solid ? " pub-header-solid" : ""}`}>
      <nav className="pub-container pub-nav" aria-label={L("Main", "الرئيسية")}>
        <Link href="/" className="pub-brand" aria-label={L("EduNexus home", "EduNexus — الصفحة الرئيسية")}>
          <BrandMark size={34} />
          <span>EduNexus</span>
        </Link>

        <div className="pub-links">
          {NAV.map((n) => <Link key={n.href} href={n.href}>{L(n.en, n.ar)}</Link>)}
        </div>

        <div className="pub-actions">
          <button type="button" className="pub-lang" onClick={() => setLocale(locale === "ar" ? "en" : "ar")}
            aria-label={locale === "ar" ? "Switch to English" : "التبديل إلى العربية"}>
            <LandingIcon name="globe" size={18} />
            <span>{locale === "ar" ? "English" : "عربي"}</span>
          </button>
          <Link href={user ? "/my-work" : "/signin"} className="pub-signin" aria-label={signInLabel} title={signInLabel}>
            <span className="pub-signin-icon"><LandingIcon name={user ? "user" : "signin"} size={18} /></span>
            <span className="pub-signin-text">{signInLabel}</span>
          </Link>
          <button type="button" className="pub-menu" onClick={() => setOpen(!open)} aria-expanded={open}
            aria-label={open ? L("Close menu", "إغلاق القائمة") : L("Open menu", "فتح القائمة")}>
            <LandingIcon name={open ? "close" : "menu"} />
          </button>
        </div>
      </nav>
      {open && (
        <div className="pub-mobile">
          {NAV.map((n) => <Link key={n.href} href={n.href} onClick={() => setOpen(false)}>{L(n.en, n.ar)}</Link>)}
          <Link href={user ? "/my-work" : "/signin"} className="btn" onClick={() => setOpen(false)}>{signInLabel}</Link>
        </div>)}
    </header>
  );
}

export function PublicFooter() {
  const L = useL();
  return (
    <footer className="pub-footer">
      <div className="pub-container pub-footer-grid">
        <div>
          <Link href="/" className="pub-brand"><BrandMark size={30} /><span>EduNexus</span></Link>
          <p className="pub-muted">{L("The institutional operating system for universities and schools: governance, quality and daily work in Arabic and English.",
            "نظام تشغيل مؤسسي للجامعات والمدارس: الحوكمة والجودة والعمل اليومي بالعربية والإنجليزية.")}</p>
        </div>
        <div>
          <h3>{L("Product", "المنتج")}</h3>
          <Link href="/#features">{L("Features", "المزايا")}</Link>
          <Link href="/#how-it-works">{L("How it works", "كيف تعمل")}</Link>
          <Link href="/#trust">{L("Trust & compliance", "الثقة والامتثال")}</Link>
        </div>
        <div>
          <h3>{L("Account", "الحساب")}</h3>
          <Link href="/signin">{L("Sign in", "تسجيل الدخول")}</Link>
          <Link href="/privacy">{L("Privacy notice", "إشعار الخصوصية")}</Link>
        </div>
      </div>
      <div className="pub-container pub-footer-base">© {new Date().getFullYear()} EduNexus</div>
    </footer>
  );
}
