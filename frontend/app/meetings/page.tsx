"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

export default function MeetingsPage() {
  const [tenantId, setTenantId] = useState("");
  const [err, setErr] = useState("");
  const [committees, setCommittees] = useState<{ id: string; code: string; name: string }[]>([]);
  const [meetings, setMeetings] = useState<{ id: string; title: string; status: string }[]>([]);
  const [decisions, setDecisions] = useState<{ id: string; text: string; status: string }[]>([]);
  const [code, setCode] = useState("");
  const [committeeId, setCommitteeId] = useState("");
  const [title, setTitle] = useState("");
  const [meetingId, setMeetingId] = useState("");
  const [decisionText, setDecisionText] = useState("");
  async function load() {
    setErr("");
    try {
      setCommittees(await api(`/api/committees?tenantId=${tenantId}`));
      setMeetings(await api(`/api/meetings?tenantId=${tenantId}`));
      setDecisions(await api(`/api/decisions?tenantId=${tenantId}`));
    } catch (e) { setErr(String(e)); }
  }
  async function createCommittee() {
    setErr("");
    try {
      await api("/api/committees", { method: "POST", body: JSON.stringify({ tenantId, code, name: code }) });
      setCode(""); await load();
    } catch (e) { setErr(String(e)); }
  }
  async function schedule() {
    setErr("");
    try {
      await api("/api/meetings", { method: "POST", body: JSON.stringify({ tenantId, committeeId, title, startsAt: new Date(Date.now() + 86400000).toISOString() }) });
      setTitle(""); await load();
    } catch (e) { setErr(String(e)); }
  }
  async function conclude() {
    setErr("");
    try {
      await api(`/api/meetings/${meetingId}/conclude`, { method: "POST", body: JSON.stringify({ tenantId, minutes: "Concluded via web." }) });
      await load();
    } catch (e) { setErr(String(e)); }
  }
  async function publishDecision() {
    setErr("");
    try {
      await api(`/api/meetings/${meetingId}/decisions`, { method: "POST", body: JSON.stringify({ tenantId, text: decisionText }) });
      setDecisionText(""); await load();
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Meetings & decisions (UX-GOV-001)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <button onClick={load}>Load all</button>
      <p style={{ color: "red" }}>{err}</p>
      <h3>Committees</h3>
      <ul>{committees.map((c) => <li key={c.id}>{c.code} — {c.name} ({c.id.slice(0, 8)})</li>)}</ul>
      <input placeholder="new committee code" value={code} onChange={(e) => setCode(e.target.value)} />{" "}
      <button onClick={createCommittee}>Create</button>
      <h3>Meetings</h3>
      <ul>{meetings.map((m) => <li key={m.id}>{m.title} [{m.status}] ({m.id.slice(0, 8)})</li>)}</ul>
      <input placeholder="committee id" value={committeeId} onChange={(e) => setCommitteeId(e.target.value)} size={38} />{" "}
      <input placeholder="meeting title" value={title} onChange={(e) => setTitle(e.target.value)} />{" "}
      <button onClick={schedule}>Schedule</button>
      <h3>Conclude + decide</h3>
      <input placeholder="meeting id" value={meetingId} onChange={(e) => setMeetingId(e.target.value)} size={38} />{" "}
      <button onClick={conclude}>Conclude</button>{" "}
      <input placeholder="decision text" value={decisionText} onChange={(e) => setDecisionText(e.target.value)} size={40} />{" "}
      <button onClick={publishDecision}>Publish decision</button>
      <h3>Decisions</h3>
      <ul>{decisions.map((d) => <li key={d.id}>{d.text} [{d.status}]</li>)}</ul>
    </main>
  );
}
