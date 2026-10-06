"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Committee = { id: string; code: string; name: string; isActive: boolean };
type Member = { id: string; personId: string; role: string; joinedAt: string; termEndsAt: string | null };
type Meeting = { id: string; title: string; startsAt: string; status: string };
type AgendaItem = { id: string; order: number; title: string; description: string | null };
type Minutes = { id: string; version: number; content: string; status: string; createdAt: string; authorId: string };
type AiResult = { summary?: string; draft?: string; proposedDecisions?: string[]; model: string };

export default function GovernancePage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [committees, setCommittees] = useState<Committee[]>([]);
  const [committee, setCommittee] = useState<Committee | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [meetings, setMeetings] = useState<Meeting[]>([]);
  const [meeting, setMeeting] = useState<Meeting | null>(null);
  const [agenda, setAgenda] = useState<AgendaItem[]>([]);
  const [minutes, setMinutes] = useState<Minutes[]>([]);
  const [newMember, setNewMember] = useState("");
  const [newRole, setNewRole] = useState("Member");
  const [newItem, setNewItem] = useState("");
  const [text, setText] = useState("");
  const [ai, setAi] = useState<AiResult | null>(null);

  const load = () => attempt(setErr, async () => setCommittees(await api<Committee[]>(`/api/committees?tenantId=${tenantId}`)));
  const openCommittee = (c: Committee) => attempt(setErr, async () => {
    const d = await api<{ committee: Committee; members: Member[]; meetings: Meeting[] }>(`/api/committees/${c.id}?tenantId=${tenantId}`);
    setCommittee(d.committee); setMembers(d.members); setMeetings(d.meetings); setMeeting(null); setAi(null);
  });
  const openMeeting = (m: Meeting) => attempt(setErr, async () => {
    setMeeting(m); setAi(null);
    setAgenda(await api<AgendaItem[]>(`/api/meetings/${m.id}/agenda?tenantId=${tenantId}`));
    setMinutes(await api<Minutes[]>(`/api/meetings/${m.id}/minutes?tenantId=${tenantId}`));
  });
  const post = (path: string, body: object, after: () => Promise<void>) => attempt(setErr, async () => {
    await api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
    await after();
  });
  const refreshCommittee = async () => { if (committee) await openCommittee(committee); };
  const refreshMeeting = async () => { if (meeting) await openMeeting(meeting); };
  const removeMember = (id: string) => attempt(setErr, async () => {
    await api(`/api/committees/${committee!.id}/members/${id}?tenantId=${tenantId}`, { method: "DELETE" });
    await refreshCommittee();
  });
  const runAi = (path: string) => attempt(setErr, async () =>
    setAi(await api<AiResult>(path, { method: "POST", body: JSON.stringify({ tenantId }) })));

  return (
    <Page title={L("Committees & Minutes", "اللجان والمحاضر")} code="UX-GOV-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load committees", "تحميل اللجان")}</button>}>
      <div className="card-grid">
        <Card title={L("Committees", "اللجان")}>
          <Table rows={committees} empty={L("No committees.", "لا لجان.")} onRowClick={openCommittee} columns={[
            { label: L("Code", "الرمز"), render: (c) => c.code },
            { label: L("Name", "الاسم"), render: (c) => c.name },
            { label: L("Active", "نشطة"), render: (c) => (c.isActive ? "✓" : "–") },
          ]} />
        </Card>
        {committee && (
          <Card title={`${committee.name} — ${L("members", "الأعضاء")}`}>
            <Table rows={members} empty={L("No members.", "لا أعضاء.")} columns={[
              { label: L("Person", "الشخص"), render: (m) => <code style={{ fontSize: "0.75rem" }}>{m.personId.slice(0, 8)}</code> },
              { label: L("Role", "الدور"), render: (m) => m.role },
              { label: L("Term ends", "نهاية العضوية"), render: (m) => fmtDate(m.termEndsAt) },
              { label: "", render: (m) => <button className="btn btn-secondary" onClick={() => removeMember(m.id)}>{L("Remove", "إزالة")}</button> },
            ]} />
            <div style={{ display: "flex", gap: "0.5rem", marginTop: "0.75rem", flexWrap: "wrap" }}>
              <input className="input" placeholder={L("Person ID", "معرّف الشخص")} value={newMember} onChange={(e) => setNewMember(e.target.value)} style={{ flex: 2 }} />
              <input className="input" value={newRole} onChange={(e) => setNewRole(e.target.value)} style={{ flex: 1 }} />
              <button className="btn" disabled={!newMember} onClick={() => post(`/api/committees/${committee.id}/members`, { personId: newMember, role: newRole }, refreshCommittee)}>
                {L("Add", "إضافة")}</button>
            </div>
            <h4>{L("Meetings", "الاجتماعات")}</h4>
            <Table rows={meetings} empty={L("No meetings.", "لا اجتماعات.")} onRowClick={openMeeting} columns={[
              { label: L("Title", "العنوان"), render: (m) => m.title },
              { label: L("Starts", "الموعد"), render: (m) => fmtDate(m.startsAt) },
              { label: L("Status", "الحالة"), render: (m) => <span className="badge">{m.status}</span> },
            ]} />
          </Card>
        )}
        {meeting && (
          <Card title={`${meeting.title} — ${L("agenda & check-in", "جدول الأعمال والحضور")}`}>
            <ol style={{ marginTop: 0 }}>{agenda.map((a) => <li key={a.id}>{a.title}{a.description ? ` — ${a.description}` : ""}</li>)}</ol>
            <div style={{ display: "flex", gap: "0.5rem" }}>
              <input className="input" placeholder={L("New agenda item", "بند جديد")} value={newItem} onChange={(e) => setNewItem(e.target.value)} />
              <button className="btn btn-secondary" disabled={!newItem} onClick={() => post(`/api/meetings/${meeting.id}/agenda-items`, { title: newItem, description: null },
                async () => { setNewItem(""); await refreshMeeting(); })}>{L("Add", "إضافة")}</button>
            </div>
            <button className="btn" style={{ marginTop: "0.75rem" }} onClick={() => post(`/api/meetings/${meeting.id}/check-in`, {}, refreshMeeting)}>
              {L("Check me in", "تسجيل حضوري")}</button>
          </Card>
        )}
        {meeting && (
          <Card title={L("Minutes", "المحضر")} wide>
            <Table rows={minutes} empty={L("No minutes yet.", "لا محضر بعد.")} onRowClick={(m) => setText(m.content)} columns={[
              { label: L("Version", "النسخة"), render: (m) => `v${m.version}` },
              { label: L("Status", "الحالة"), render: (m) => <span className="badge">{m.status}</span> },
              { label: L("Saved", "الحفظ"), render: (m) => fmtDate(m.createdAt) },
              { label: "", render: (m) => m.status === "Submitted"
                  ? <button className="btn" onClick={() => post(`/api/minutes/${m.id}/approve`, {}, refreshMeeting)}>{L("Approve", "اعتماد")}</button>
                  : m.status === "Approved" ? "✓"
                  : <button className="btn btn-secondary" onClick={() => runAi(`/api/ai/minutes/${m.id}/extract-decisions`)}>{L("AI: extract decisions", "ذكاء: استخراج القرارات")}</button> },
            ]} />
            <Field label={L("Minutes text", "نص المحضر")}><textarea className="input" rows={10} value={text} onChange={(e) => setText(e.target.value)} /></Field>
            <div style={{ display: "flex", gap: "0.5rem", marginTop: "0.75rem", flexWrap: "wrap" }}>
              <button className="btn btn-secondary" disabled={!text} onClick={() => post(`/api/meetings/${meeting.id}/minutes`, { content: text, submit: false }, refreshMeeting)}>
                {L("Save draft", "حفظ مسودة")}</button>
              <button className="btn" disabled={!text} onClick={() => post(`/api/meetings/${meeting.id}/minutes`, { content: text, submit: true }, refreshMeeting)}>
                {L("Submit for approval", "إرسال للاعتماد")}</button>
              <button className="btn btn-secondary" onClick={() => runAi(`/api/ai/meetings/${meeting.id}/summary`)}>{L("AI: summary", "ذكاء: ملخص")}</button>
              <button className="btn btn-secondary" onClick={() => runAi(`/api/ai/meetings/${meeting.id}/draft-minutes`)}>{L("AI: draft minutes", "ذكاء: مسودة محضر")}</button>
            </div>
            {ai && (
              <div style={{ marginTop: "1rem", padding: "1rem", border: "1px dashed rgba(148,163,184,0.5)", borderRadius: "0.5rem" }}>
                <div style={{ fontSize: "0.8rem", opacity: 0.75 }}>
                  {L("AI draft — review before use", "مسودة آلية — راجعها قبل الاستخدام")} · {ai.model}</div>
                {ai.summary && <p>{ai.summary}</p>}
                {ai.draft && <>
                  <pre style={{ whiteSpace: "pre-wrap" }}>{ai.draft}</pre>
                  <button className="btn btn-secondary" onClick={() => setText(ai.draft!)}>{L("Use as minutes text", "استخدمه نصًا للمحضر")}</button></>}
                {ai.proposedDecisions && <ul>{ai.proposedDecisions.map((d) => <li key={d}>{d}</li>)}</ul>}
              </div>
            )}
          </Card>
        )}
      </div>
    </Page>
  );
}
