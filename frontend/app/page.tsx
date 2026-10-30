"use client";
import Link from "next/link";
import { useEffect, useRef } from "react";
import { GeometricPattern, LandingIcon, type LandingIconName } from "../components/LandingIcon";
import { useSignedInName } from "../components/PublicChrome";
import { useTranslation } from "../components/TranslationProvider";

/**
 * The public home page at "/". It says what EduNexus does in plain words and sends people to sign in.
 * Every claim is something the product does today: no usage numbers or testimonials until there are real ones.
 */

type Bi = { en: string; ar: string };

/** Facts about the product (not usage statistics). */
const FACTS: Array<{ icon: LandingIconName; value: Bi; label: Bi }> = [
  { icon: "globe", value: { en: "AR · EN", ar: "عربي · EN" }, label: { en: "Full Arabic (RTL) and English", ar: "عربية كاملة (من اليمين) وإنجليزية" } },
  { icon: "calendar", value: { en: "Hijri", ar: "هجري" }, label: { en: "Umm al-Qura dates beside Gregorian", ar: "تقويم أم القرى بجانب الميلادي" } },
  { icon: "award", value: { en: "3", ar: "٣" }, label: { en: "Accreditation frameworks built in", ar: "أطر اعتماد مدمجة" } },
  { icon: "shield", value: { en: "PDPL", ar: "PDPL" }, label: { en: "Data-subject rights built in", ar: "حقوق أصحاب البيانات مدمجة" } },
];

/** Sign in by role: the same institution account, landing on the screen each role starts from. */
const ROLES: Array<{ key: string; icon: LandingIconName; next: string; title: Bi; lead: Bi; points: Bi[] }> = [
  {
    key: "staff", icon: "inbox", next: "/my-work",
    title: { en: "Staff & faculty", ar: "الموظفون وأعضاء هيئة التدريس" },
    lead: { en: "One inbox for everything waiting on you.", ar: "صندوق واحد لكل ما ينتظرك." },
    points: [
      { en: "Approvals, tasks and letters in one list", ar: "الموافقات والمهام والمراسلات في قائمة واحدة" },
      { en: "Requests with forms and tracked deadlines", ar: "طلبات بنماذج ومواعيد متابعة" },
      { en: "Policies to read and acknowledge", ar: "سياسات للاطلاع والإقرار" },
    ],
  },
  {
    key: "board", icon: "vote", next: "/board",
    title: { en: "Leadership & boards", ar: "القيادات والمجالس" },
    lead: { en: "Meetings that end in decisions, and decisions that get done.", ar: "اجتماعات تنتهي بقرارات، وقرارات تُنفَّذ." },
    points: [
      { en: "Agendas, quorum, votes and approved minutes", ar: "جداول الأعمال والنصاب والتصويت والمحاضر المعتمدة" },
      { en: "Conflict-of-interest recusal and written resolutions", ar: "التنحي لتعارض المصالح والقرارات بالتمرير" },
      { en: "Decision actions followed to verified evidence", ar: "متابعة تنفيذ القرارات حتى التحقق بالشواهد" },
    ],
  },
  {
    key: "quality", icon: "award", next: "/accreditation",
    title: { en: "Quality & accreditation", ar: "الجودة والاعتماد" },
    lead: { en: "Evidence collected all year, not the month before the visit.", ar: "شواهد تُجمع طوال العام، لا قبل الزيارة بشهر." },
    points: [
      { en: "NCAAA and ABET standards in one click", ar: "معايير NCAAA وABET بنقرة واحدة" },
      { en: "One record cited across several frameworks", ar: "سجل واحد شاهدًا لعدة أطر" },
      { en: "Self-study report in Arabic or English", ar: "تقرير الدراسة الذاتية بالعربية أو الإنجليزية" },
    ],
  },
  {
    key: "admin", icon: "key", next: "/dashboard",
    title: { en: "Administrators", ar: "مديرو النظام" },
    lead: { en: "People, roles and controls for the whole institution.", ar: "الأشخاص والأدوار والضوابط للمؤسسة كلها." },
    points: [
      { en: "Directory, roles and delegations", ar: "الدليل والأدوار والتفويضات" },
      { en: "Audit trail of every action", ar: "سجل تدقيق لكل إجراء" },
      { en: "Retention schedules and privacy requests", ar: "جداول الحفظ وطلبات الخصوصية" },
    ],
  },
];

const STEPS: Array<{ icon: LandingIconName; title: Bi; desc: Bi }> = [
  { icon: "key", title: { en: "Connect your sign-in", ar: "اربط تسجيل الدخول" }, desc: { en: "Staff use the institution account they already have, with multi-factor authentication.", ar: "يستخدم الموظفون حساب المؤسسة الحالي مع التحقق متعدد العوامل." } },
  { icon: "people", title: { en: "Bring in your people", ar: "أضف منسوبيك" }, desc: { en: "Departments, roles and committees, with delegations for when someone is away.", ar: "الأقسام والأدوار واللجان، مع التفويض عند الغياب." } },
  { icon: "book", title: { en: "Load your rules", ar: "حمّل أنظمتك" }, desc: { en: "Policies, approval routes, retention schedules and accreditation standards.", ar: "السياسات ومسارات الاعتماد وجداول الحفظ ومعايير الاعتماد الأكاديمي." } },
  { icon: "chart", title: { en: "Work, decide, report", ar: "اعمل، قرّر، تابع" }, desc: { en: "Daily work flows through EduNexus, and leaders see where things stand.", ar: "يمر العمل اليومي عبر EduNexus، وترى القيادات موقف كل شيء." } },
];

const FEATURES: Array<{ icon: LandingIconName; tone: string; title: Bi; desc: Bi }> = [
  { icon: "mail", tone: "blue", title: { en: "Correspondence", ar: "المراسلات" }, desc: { en: "Numbered incoming, outgoing and internal letters with routing slips and confidential handling.", ar: "مراسلات واردة وصادرة وداخلية مرقّمة، مع إحالات وتعامل سري." } },
  { icon: "check", tone: "green", title: { en: "Requests & approvals", ar: "الطلبات والموافقات" }, desc: { en: "Form-based requests, approval chains and escalation when a deadline slips.", ar: "طلبات بنماذج، وسلاسل موافقة، وتصعيد عند تأخر الموعد." } },
  { icon: "tasks", tone: "amber", title: { en: "Tasks & my work", ar: "المهام وعملي" }, desc: { en: "Everything waiting on you in one inbox, with evidence and verification.", ar: "كل ما ينتظرك في صندوق واحد، مع الشواهد والتحقق." } },
  { icon: "people", tone: "violet", title: { en: "Committees & meetings", ar: "اللجان والاجتماعات" }, desc: { en: "Agendas, attendance, quorum, minutes drafts and minutes approval.", ar: "جداول الأعمال والحضور والنصاب ومسودات المحاضر واعتمادها." } },
  { icon: "vote", tone: "teal", title: { en: "Board", ar: "مجلس الإدارة" }, desc: { en: "Member-only voting, conflict recusal, an interest register and written resolutions.", ar: "تصويت للأعضاء فقط، وتنحٍّ لتعارض المصالح، وسجل الإفصاح، وقرارات بالتمرير." } },
  { icon: "scale", tone: "red", title: { en: "Decisions", ar: "القرارات" }, desc: { en: "Every decision tracked to its actions, owners, evidence and verification.", ar: "متابعة كل قرار حتى إجراءاته ومسؤوليه وشواهده والتحقق منه." } },
  { icon: "book", tone: "blue", title: { en: "Policies", ar: "السياسات" }, desc: { en: "Versions, review and legal workflow, quizzes and yearly re-attestation.", ar: "النسخ ومسار المراجعة القانونية والاختبارات وإعادة الإقرار السنوي." } },
  { icon: "award", tone: "green", title: { en: "Accreditation", ar: "الاعتماد الأكاديمي" }, desc: { en: "NCAAA and ABET standards, an evidence crosswalk, findings and self-study reports.", ar: "معايير NCAAA وABET، ومصفوفة الشواهد، والملاحظات، وتقارير الدراسة الذاتية." } },
  { icon: "target", tone: "amber", title: { en: "Strategy", ar: "الاستراتيجية" }, desc: { en: "Plans, objectives and KPIs with direction-aware status and trends.", ar: "خطط وأهداف ومؤشرات أداء بحالة واتجاه واضحين." } },
  { icon: "archive", tone: "violet", title: { en: "Records retention", ar: "حفظ السجلات" }, desc: { en: "Retention schedules, legal holds and two-person approved disposal.", ar: "جداول الحفظ والحجز القانوني والإتلاف بموافقة شخصين." } },
  { icon: "chart", tone: "teal", title: { en: "Reports & dashboards", ar: "التقارير ولوحات المتابعة" }, desc: { en: "Executive, department and governance dashboards; exports with Hijri dates.", ar: "لوحات تنفيذية وللأقسام وللحوكمة؛ وتصدير بالتاريخ الهجري." } },
  { icon: "sparkle", tone: "red", title: { en: "AI assistants", ar: "المساعدات الذكية" }, desc: { en: "Meeting summaries, minutes drafts and decision extraction — every use logged.", ar: "ملخصات الاجتماعات ومسودات المحاضر واستخراج القرارات — مع تسجيل كل استخدام." } },
];

const TRUST: Array<{ icon: LandingIconName; text: Bi }> = [
  { icon: "lock", text: { en: "Each institution's data isolated at the database (row-level security)", ar: "عزل بيانات كل مؤسسة على مستوى قاعدة البيانات" } },
  { icon: "key", text: { en: "Single sign-on with multi-factor authentication", ar: "دخول موحّد مع التحقق متعدد العوامل" } },
  { icon: "history", text: { en: "An audit trail of every action", ar: "سجل تدقيق لكل إجراء" } },
  { icon: "shield", text: { en: "PDPL rights built in: data export, erasure, privacy notice", ar: "حقوق نظام حماية البيانات الشخصية: التصدير والإتلاف وإشعار الخصوصية" } },
  { icon: "check", text: { en: "Mapped to NCA Essential Cybersecurity Controls (ECC-2:2024)", ar: "مطابقة مع الضوابط الأساسية للأمن السيبراني (ECC-2:2024)" } },
  { icon: "building", text: { en: "Runs in your own data centre or a Saudi cloud region", ar: "يعمل في مركز بياناتك أو في سحابة داخل المملكة" } },
];

/** Blocks fade in as they scroll into view; the hero never does, and nothing moves with reduced motion. */
function useReveal() {
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = root.current;
    if (!el || window.matchMedia("(prefers-reduced-motion: reduce)").matches || !("IntersectionObserver" in window)) return;
    const items = Array.from(el.querySelectorAll<HTMLElement>("[data-reveal]"));
    items.forEach((i) => i.classList.add("pub-hidden"));
    const io = new IntersectionObserver((entries) => entries.forEach((e) => {
      if (e.isIntersecting) { e.target.classList.remove("pub-hidden"); io.unobserve(e.target); }
    }), { threshold: 0.1, rootMargin: "0px 0px -40px 0px" });
    items.forEach((i) => io.observe(i));
    return () => io.disconnect();
  }, []);
  return root;
}

export default function HomePage() {
  const { locale } = useTranslation();
  const L = (b: Bi) => (locale === "ar" ? b.ar : b.en);
  const T = (en: string, ar: string) => (locale === "ar" ? ar : en);
  const user = useSignedInName();
  const root = useReveal();
  const signInHref = user ? "/my-work" : "/signin";

  return (
    <div ref={root}>
      {/* ===== HERO ===== */}
      <section className="pub-hero">
        <GeometricPattern className="pub-pattern pub-pattern-a" />
        <GeometricPattern className="pub-pattern pub-pattern-b" />
        <div className="pub-container pub-hero-grid">
          <div className="pub-hero-text">
            <p className="pub-eyebrow"><span className="pub-dot" aria-hidden="true" />
              {T("The institutional operating system for education", "نظام التشغيل المؤسسي للتعليم")}</p>
            <h1>
              {T("Governance, quality and daily work", "الحوكمة والجودة والعمل اليومي")}
              <span className="pub-hero-mark">{T("in one place.", "في مكان واحد.")}</span>
            </h1>
            <p className="pub-lead">
              {T("Correspondence, approvals, committees and boards, policies, accreditation, strategy and records — for universities and schools, in Arabic and English.",
                 "المراسلات والموافقات واللجان والمجالس والسياسات والاعتماد الأكاديمي والاستراتيجية والسجلات — للجامعات والمدارس، بالعربية والإنجليزية.")}
            </p>
            <div className="pub-cta-row">
              <Link href={signInHref} className="pub-btn pub-btn-primary">
                <LandingIcon name={user ? "user" : "signin"} size={20} />
                {user ? T("Open my workspace", "افتح مساحة عملي") : T("Sign in", "تسجيل الدخول")}
              </Link>
              <Link href="#features" className="pub-btn pub-btn-ghost">{T("See what's inside", "تعرّف على المزايا")}</Link>
            </div>
            <ul className="pub-fineprint">
              <li><LandingIcon name="check" size={16} />{T("Arabic, right-to-left", "عربية من اليمين إلى اليسار")}</li>
              <li><LandingIcon name="check" size={16} />{T("Hijri and Gregorian dates", "التاريخ الهجري والميلادي")}</li>
              <li><LandingIcon name="check" size={16} />{T("Your institution's own sign-in", "تسجيل دخول مؤسستك")}</li>
            </ul>
          </div>

          {/* A picture of the workspace: what a staff member sees on arrival. Decoration only. */}
          <div className="pub-hero-art" aria-hidden="true">
            <div className="pub-mock">
              <div className="pub-mock-head">
                <span className="pub-mock-dots"><i /><i /><i /></span>
                <span>{T("My work", "عملي")}</span>
              </div>
              <div className="pub-mock-body">
                {[
                  { icon: "check" as const, tone: "green", t: T("Approve: lab equipment purchase", "اعتماد: شراء أجهزة المختبر"), s: T("Due today", "مستحق اليوم") },
                  { icon: "vote" as const, tone: "violet", t: T("Board resolution awaiting your vote", "قرار مجلس بانتظار تصويتك"), s: T("Closes in 2 days", "يُغلق بعد يومين") },
                  { icon: "book" as const, tone: "blue", t: T("Acknowledge: IT acceptable use v2", "إقرار: سياسة الاستخدام المقبول v2"), s: T("Quiz · 2 questions", "اختبار · سؤالان") },
                  { icon: "mail" as const, tone: "amber", t: T("Letter OUT-0142 for your review", "المراسلة OUT-0142 للمراجعة"), s: T("From the Dean's office", "من مكتب العميد") },
                ].map((r) => (
                  <div key={r.t} className="pub-mock-row">
                    <span className={`pub-tile pub-tone-${r.tone}`}><LandingIcon name={r.icon} size={18} /></span>
                    <span className="pub-mock-text"><b>{r.t}</b><small>{r.s}</small></span>
                  </div>))}
                <div className="pub-mock-progress"><span>{T("NCAAA readiness", "جاهزية NCAAA")}</span><div><i style={{ width: "72%" }} /></div><b>72%</b></div>
              </div>
            </div>
            <span className="pub-chip pub-chip-1"><LandingIcon name="check" size={16} />{T("Resolution passed · 4 of 5", "اعتُمد القرار · ٤ من ٥")}</span>
            <span className="pub-chip pub-chip-2"><LandingIcon name="calendar" size={16} />{T("Senate · 12 Rabi' II 1448", "مجلس الجامعة · ١٢ ربيع الآخر ١٤٤٨")}</span>
          </div>
        </div>
      </section>

      {/* ===== FACTS ===== */}
      <section className="pub-facts">
        <div className="pub-container pub-facts-grid" data-reveal>
          {FACTS.map((f) => (
            <div key={f.label.en} className="pub-fact">
              <LandingIcon name={f.icon} size={22} />
              <b>{L(f.value)}</b>
              <span>{L(f.label)}</span>
            </div>))}
        </div>
      </section>

      {/* ===== ROLES (sign in to your space) ===== */}
      <section id="roles" className="pub-section">
        <div className="pub-container">
          <div className="pub-section-head" data-reveal>
            <h2>{T("Built for every role in the institution", "مصمم لكل دور في المؤسسة")}</h2>
            <p>{T("One account. Each person starts from the screen that matters to them.", "حساب واحد، ويبدأ كل شخص من الشاشة التي تهمّه.")}</p>
          </div>
          <div className="pub-roles">
            {ROLES.map((r) => (
              <article key={r.key} className="pub-card pub-role" data-reveal>
                <span className="pub-tile pub-tile-lg"><LandingIcon name={r.icon} /></span>
                <h3>{L(r.title)}</h3>
                <p className="pub-muted">{L(r.lead)}</p>
                <ul>{r.points.map((p) => <li key={p.en}><LandingIcon name="check" size={16} />{L(p)}</li>)}</ul>
                <Link href={user ? r.next : `/signin?next=${encodeURIComponent(r.next)}`} className="pub-role-link">
                  <LandingIcon name="signin" size={18} />{T("Sign in", "تسجيل الدخول")}
                  <LandingIcon name="arrow" size={16} className="pub-flip" />
                </Link>
              </article>))}
          </div>
        </div>
      </section>

      {/* ===== HOW IT WORKS ===== */}
      <section id="how-it-works" className="pub-section pub-band">
        <GeometricPattern className="pub-pattern pub-pattern-c" />
        <div className="pub-container">
          <div className="pub-section-head" data-reveal>
            <h2>{T("How EduNexus works", "كيف يعمل EduNexus")}</h2>
            <p>{T("Four steps from your current way of working to one shared system.", "أربع خطوات من طريقة العمل الحالية إلى نظام واحد مشترك.")}</p>
          </div>
          <ol className="pub-steps">
            {STEPS.map((s, i) => (
              <li key={s.title.en} className="pub-step" data-reveal>
                <span className="pub-step-num">{locale === "ar" ? "١٢٣٤"[i] : i + 1}</span>
                <LandingIcon name={s.icon} size={26} />
                <h3>{L(s.title)}</h3>
                <p>{L(s.desc)}</p>
              </li>))}
          </ol>
        </div>
      </section>

      {/* ===== FEATURES ===== */}
      <section id="features" className="pub-section">
        <div className="pub-container">
          <div className="pub-section-head" data-reveal>
            <h2>{T("Everything the institution runs on", "كل ما تحتاجه المؤسسة لتعمل")}</h2>
            <p>{T("Twelve modules that share one directory, one audit trail and one inbox.", "اثنتا عشرة وحدة تشترك في دليل واحد وسجل تدقيق واحد وصندوق عمل واحد.")}</p>
          </div>
          <div className="pub-features">
            {FEATURES.map((f) => (
              <article key={f.title.en} className="pub-card pub-feature" data-reveal>
                <span className={`pub-tile pub-tone-${f.tone}`}><LandingIcon name={f.icon} /></span>
                <h3>{L(f.title)}</h3>
                <p>{L(f.desc)}</p>
              </article>))}
          </div>
        </div>
      </section>

      {/* ===== TRUST ===== */}
      <section id="trust" className="pub-section pub-soft">
        <div className="pub-container">
          <div className="pub-section-head" data-reveal>
            <h2>{T("Trust and compliance, by design", "الثقة والامتثال من الأساس")}</h2>
            <p>{T("Built for Saudi requirements: personal data protection and national cybersecurity controls.", "مبني لمتطلبات المملكة: حماية البيانات الشخصية وضوابط الأمن السيبراني الوطنية.")}</p>
          </div>
          <ul className="pub-trust">
            {TRUST.map((t) => (
              <li key={t.text.en} className="pub-card" data-reveal>
                <span className="pub-tile pub-tone-green"><LandingIcon name={t.icon} size={20} /></span>
                <span>{L(t.text)}</span>
              </li>))}
          </ul>
        </div>
      </section>

      {/* ===== FINAL CTA ===== */}
      <section className="pub-section">
        <div className="pub-container">
          <div className="pub-final" data-reveal>
            <GeometricPattern className="pub-pattern pub-pattern-d" />
            <LandingIcon name="sparkle" size={36} />
            <h2>{user ? T("Welcome back", "مرحبًا بعودتك") : T("Ready to start?", "جاهز للبدء؟")}</h2>
            <p>{T("Sign in with your institution account. Your administrator sets up access; there is no public sign-up.",
                  "سجّل الدخول بحساب مؤسستك. يتولى مدير النظام منح الصلاحيات؛ لا يوجد تسجيل عام.")}</p>
            <Link href={signInHref} className="pub-btn pub-btn-primary">
              <LandingIcon name={user ? "user" : "signin"} size={20} />
              {user ? T("Open my workspace", "افتح مساحة عملي") : T("Sign in", "تسجيل الدخول")}
            </Link>
          </div>
        </div>
      </section>
    </div>
  );
}
