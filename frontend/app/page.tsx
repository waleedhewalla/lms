"use client";
import Link from "next/link";
import { useTranslation } from "../components/TranslationProvider";

export default function Home() {
  const { t } = useTranslation();

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("app.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>{t("app.subtitle")}</p>
        </div>
      </div>

      <div className="card-grid">
        <div className="glass-card">
          <span className="badge">UX-DIR-001</span>
          <h3>{t("dash.directory.title")}</h3>
          <p>{t("dash.directory.desc")}</p>
          <Link href="/directory" className="btn" style={{ marginTop: 'auto' }}>{t("dash.directory.btn")}</Link>
        </div>

        <div className="glass-card">
          <span className="badge">UX-ADM-001</span>
          <h3>{t("dash.roles.title")}</h3>
          <p>{t("dash.roles.desc")}</p>
          <Link href="/roles" className="btn" style={{ marginTop: 'auto' }}>{t("dash.roles.btn")}</Link>
        </div>

        <div className="glass-card">
          <span className="badge">System</span>
          <h3>{t("dash.audit.title")}</h3>
          <p>{t("dash.audit.desc")}</p>
          <Link href="/audit" className="btn" style={{ marginTop: 'auto' }}>{t("dash.audit.btn")}</Link>
        </div>

        <div className="glass-card">
          <span className="badge">UX-COR-001</span>
          <h3>{t("dash.cor.title")}</h3>
          <p>{t("dash.cor.desc")}</p>
          <Link href="/correspondence" className="btn btn-secondary" style={{ marginTop: 'auto' }}>{t("dash.cor.btn")}</Link>
        </div>
        
        <div className="glass-card">
          <span className="badge">UX-APR-006</span>
          <h3>{t("dash.apr.title")}</h3>
          <p>{t("dash.apr.desc")}</p>
          <Link href="/approvals" className="btn btn-secondary" style={{ marginTop: 'auto' }}>{t("dash.apr.btn")}</Link>
        </div>
        
        <div className="glass-card">
          <span className="badge">UX-GOV-001</span>
          <h3>{t("dash.gov.title")}</h3>
          <p>{t("dash.gov.desc")}</p>
          <Link href="/meetings" className="btn btn-secondary" style={{ marginTop: 'auto' }}>{t("dash.gov.btn")}</Link>
        </div>
      </div>
    </div>
  );
}