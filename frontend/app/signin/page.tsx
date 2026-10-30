"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { oidcEnabled } from "../../lib/config";
import { signIn, signOut } from "../../lib/oidc";
import { BrandMark, LandingIcon, type LandingIconName } from "../../components/LandingIcon";
import { useSignedInName } from "../../components/PublicChrome";
import { useTranslation } from "../../components/TranslationProvider";

/** Where each kind of user starts. Everyone signs in with the same institution account (SSO + MFA). */
const CHOICES: Array<{ next: string; icon: LandingIconName; en: [string, string]; ar: [string, string] }> = [
  { next: "/my-work", icon: "inbox", en: ["Staff & faculty", "Your inbox of approvals, tasks and letters"], ar: ["الموظفون وأعضاء هيئة التدريس", "صندوق الموافقات والمهام والمراسلات"] },
  { next: "/board", icon: "vote", en: ["Leadership & boards", "Meetings, votes and written resolutions"], ar: ["القيادات والمجالس", "الاجتماعات والتصويت والقرارات بالتمرير"] },
  { next: "/accreditation", icon: "award", en: ["Quality & accreditation", "Standards, evidence and self-study"], ar: ["الجودة والاعتماد", "المعايير والشواهد والدراسة الذاتية"] },
  { next: "/dashboard", icon: "key", en: ["Administrators", "People, roles, audit and settings"], ar: ["مديرو النظام", "الأشخاص والأدوار والتدقيق والإعدادات"] },
];

/** Only same-origin paths may be a destination ("//host" would leave the site). */
const safeNext = (p: string | null) => (p && /^\/(?![\/\\])/.test(p) ? p : "/my-work");

export default function SignInPage() {
  const { locale } = useTranslation();
  const T = (en: string, ar: string) => (locale === "ar" ? ar : en);
  const user = useSignedInName();
  const [next, setNext] = useState("/my-work");
  const [sso, setSso] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    setNext(safeNext(new URLSearchParams(window.location.search).get("next")));
    setSso(oidcEnabled());
  }, []);

  const go = async () => {
    setError(""); setBusy(true);
    try { await signIn(next); } catch (e) { setError(String(e)); setBusy(false); }
  };

  return (
    <div className="pub-signin-page">
      <div className="pub-card pub-signin-card">
        <div className="pub-signin-brand"><BrandMark size={48} /></div>
        <h1>{T("Sign in to EduNexus", "تسجيل الدخول إلى EduNexus")}</h1>

        {user ? (<>
          <p className="pub-muted">{T("You are signed in as", "أنت مسجّل الدخول باسم")} <b>{user}</b>.</p>
          <Link href={next} className="pub-btn pub-btn-primary pub-btn-block"><LandingIcon name="arrow" size={20} className="pub-flip" />{T("Continue to my workspace", "المتابعة إلى مساحة عملي")}</Link>
          <button type="button" className="pub-btn pub-btn-quiet pub-btn-block" onClick={() => signOut()}>{T("Sign out", "تسجيل الخروج")}</button>
        </>) : (<>
          <p className="pub-muted">{T("Choose where you want to start. Everyone uses the same institution account.", "اختر من أين تبدأ. يستخدم الجميع حساب المؤسسة نفسه.")}</p>
          <fieldset className="pub-choices">
            <legend className="pub-sr">{T("Start in", "ابدأ من")}</legend>
            {CHOICES.map((c) => {
              const [title, desc] = locale === "ar" ? c.ar : c.en;
              return (
                <label key={c.next} className={`pub-choice${next === c.next ? " pub-choice-on" : ""}`}>
                  <input type="radio" name="start" value={c.next} checked={next === c.next} onChange={() => setNext(c.next)} />
                  <span className="pub-tile"><LandingIcon name={c.icon} size={20} /></span>
                  <span><b>{title}</b><small>{desc}</small></span>
                </label>);
            })}
          </fieldset>

          {sso ? (
            <button type="button" className="pub-btn pub-btn-primary pub-btn-block" onClick={go} disabled={busy}>
              <LandingIcon name="signin" size={20} />
              {busy ? T("Redirecting…", "جارٍ التحويل…") : T("Continue with your institution account", "المتابعة بحساب المؤسسة")}
            </button>
          ) : (
            <div className="pub-note" role="note">
              <b>{T("Single sign-on is not configured on this server.", "لم يُضبط الدخول الموحّد على هذا الخادم.")}</b>
              <span>{T("In development, open the workspace and use the connection bar to mint a test token.", "في بيئة التطوير، افتح مساحة العمل واستخدم شريط الاتصال لإنشاء رمز تجريبي.")}</span>
              <Link href={next} className="pub-btn pub-btn-primary pub-btn-block"><LandingIcon name="arrow" size={20} className="pub-flip" />{T("Open the workspace", "افتح مساحة العمل")}</Link>
            </div>)}
          {error && <p role="alert" className="pub-error">{error}</p>}
          <p className="pub-fine">
            <LandingIcon name="lock" size={14} />
            {T("Protected by your institution's sign-in and multi-factor authentication. No public sign-up.", "محمي بتسجيل دخول مؤسستك والتحقق متعدد العوامل. لا يوجد تسجيل عام.")}
          </p>
        </>)}
      </div>
      <p className="pub-signin-foot"><Link href="/">{T("Back to home", "العودة إلى الرئيسية")}</Link> · <Link href="/privacy">{T("Privacy notice", "إشعار الخصوصية")}</Link></p>
    </div>
  );
}
