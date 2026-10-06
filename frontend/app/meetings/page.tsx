"use client";
import { useState, useEffect } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTranslation } from "../../components/TranslationProvider";
import { Chatter } from "../../components/Chatter";

type AgendaItem = {
  id: string;
  order: number;
  title: string;
  description?: string;
};

type Attendance = {
  id: string;
  personId: string;
  status: "Present" | "Absent" | "Excused";
};

type MeetingDetails = {
  meeting: {
    id: string;
    committeeId: string;
    title: string;
    startsAt: string;
    status: string;
    minutes?: string;
  };
  agenda: AgendaItem[];
  attendances: Attendance[];
};

type QuorumInfo = {
  totalMembers: number;
  presentCount: number;
  absentCount: number;
  excusedCount: number;
  quorumRequired: number;
  hasQuorum: boolean;
};

type VoteTally = {
  agendaItemId: string;
  inFavor: number;
  against: number;
  abstain: number;
  totalVotes: number;
};

export default function MeetingsPage() {
  const { t } = useTranslation();
  const [tenantId, setTenantId] = useState("");
  const [err, setErr] = useState("");
  const [committees, setCommittees] = useState<{ id: string; code: string; name: string }[]>([]);
  const [meetings, setMeetings] = useState<{ id: string; title: string; status: string; committeeId: string; startsAt: string }[]>([]);
  const [decisions, setDecisions] = useState<{ id: string; text: string; status: string; meetingId: string }[]>([]);
  
  // Creation form state
  const [code, setCode] = useState("");
  const [committeeId, setCommitteeId] = useState("");
  const [title, setTitle] = useState("");
  const [agendaTitle, setAgendaTitle] = useState("");
  
  // Active Board Session state (Azeus Convene pattern)
  const [activeMeetingId, setActiveMeetingId] = useState<string | null>(null);
  const [meetingDetail, setMeetingDetail] = useState<MeetingDetails | null>(null);
  const [quorum, setQuorum] = useState<QuorumInfo | null>(null);
  const [tallies, setTallies] = useState<VoteTally[]>([]);
  const [votingPersonId, setVotingPersonId] = useState("");
  const [voteRemarks, setVoteRemarks] = useState("");
  const [selectedAgendaId, setSelectedAgendaId] = useState<string | null>(null);
  const [decisionText, setDecisionText] = useState("");
  const [minutesText, setMinutesText] = useState("");
  const [packetModalOpen, setPacketModalOpen] = useState(false);
  const [packetData, setPacketData] = useState<{
    meeting: { id: string; title: string; status: string; startsAt: string; minutes?: string };
    committee?: { code: string; name: string };
    governance: { totalMembers: number; presentCount: number; absentCount: number; excusedCount: number; quorumRequired: number; hasQuorum: boolean };
    members: { personId: string; role: string }[];
    attendances: { personId: string; status: string }[];
    agenda: { id: string; order: number; title: string; description?: string }[];
    votingTallies: { agendaItemId: string; inFavor: number; against: number; abstain: number; totalVotes: number }[];
    decisions: { id: string; text: string; status: string }[];
    actionItems: { id: string; description: string; status: string }[];
    certification: { isConcluded: boolean; hasMinutes: boolean; minutes?: string };
    generatedAt: string;
  } | null>(null);

  async function openBoardPacket() {
    if (!activeMeetingId) return;
    setErr("");
    try {
      const p = await api<any>(`/api/meetings/${activeMeetingId}/packet?tenantId=${tenantId}`);
      setPacketData(p);
      setPacketModalOpen(true);
    } catch (e) {
      setErr(String(e));
    }
  }

  async function load() {
    setErr("");
    try {
      const [c, m, d] = await Promise.all([
        api<{ id: string; code: string; name: string }[]>(`/api/committees?tenantId=${tenantId}`),
        api<{ id: string; title: string; status: string; committeeId: string; startsAt: string }[]>(`/api/meetings?tenantId=${tenantId}`),
        api<{ id: string; text: string; status: string; meetingId: string }[]>(`/api/decisions?tenantId=${tenantId}`)
      ]);
      setCommittees(c);
      setMeetings(m);
      setDecisions(d);
      if (m.length > 0 && !activeMeetingId) {
        selectMeeting(m[0].id);
      }
    } catch (e) { setErr(String(e)); }
  }

  async function selectMeeting(id: string) {
    setActiveMeetingId(id);
    setErr("");
    try {
      const [detail, q, v] = await Promise.all([
        api<MeetingDetails>(`/api/meetings/${id}?tenantId=${tenantId}`),
        api<QuorumInfo>(`/api/meetings/${id}/quorum?tenantId=${tenantId}`).catch(() => null),
        api<{ votes: unknown[]; tallies: VoteTally[] }>(`/api/meetings/${id}/votes?tenantId=${tenantId}`).catch(() => ({ votes: [], tallies: [] }))
      ]);
      setMeetingDetail(detail);
      setQuorum(q);
      setTallies(v.tallies || []);
      if (detail.agenda.length > 0 && !selectedAgendaId) {
        setSelectedAgendaId(detail.agenda[0].id);
      }
    } catch (e) { setErr(String(e)); }
  }

  async function createCommittee() {
    setErr("");
    try {
      await api("/api/committees", { method: "POST", body: JSON.stringify({ tenantId, code, name: code }) });
      setCode("");
      await load();
    } catch (e) { setErr(String(e)); }
  }

  async function schedule() {
    setErr("");
    try {
      const agenda = agendaTitle ? [{ title: agendaTitle, description: "Scheduled item" }] : [];
      await api("/api/meetings", {
        method: "POST",
        body: JSON.stringify({
          tenantId,
          committeeId,
          title,
          startsAt: new Date(Date.now() + 86400000).toISOString(),
          agenda
        })
      });
      setTitle("");
      setAgendaTitle("");
      await load();
    } catch (e) { setErr(String(e)); }
  }

  async function castVote(choice: "InFavor" | "Against" | "Abstain") {
    if (!activeMeetingId || !selectedAgendaId) return;
    setErr("");
    try {
      await api(`/api/meetings/${activeMeetingId}/votes`, {
        method: "POST",
        body: JSON.stringify({
          tenantId,
          agendaItemId: selectedAgendaId,
          personId: votingPersonId || "00000000-0000-0000-0000-000000000001",
          choice,
          remarks: voteRemarks || undefined
        })
      });
      setVoteRemarks("");
      // Refresh votes
      const v = await api<{ votes: unknown[]; tallies: VoteTally[] }>(`/api/meetings/${activeMeetingId}/votes?tenantId=${tenantId}`);
      setTallies(v.tallies || []);
    } catch (e) { setErr(String(e)); }
  }

  async function concludeMeeting() {
    if (!activeMeetingId) return;
    setErr("");
    try {
      await api(`/api/meetings/${activeMeetingId}/conclude`, {
        method: "POST",
        body: JSON.stringify({ tenantId, minutes: minutesText || "Minutes confirmed and concluded." })
      });
      setMinutesText("");
      await load();
      await selectMeeting(activeMeetingId);
    } catch (e) { setErr(String(e)); }
  }

  async function publishDecision() {
    if (!activeMeetingId || !decisionText) return;
    setErr("");
    try {
      await api(`/api/meetings/${activeMeetingId}/decisions`, {
        method: "POST",
        body: JSON.stringify({ tenantId, text: decisionText })
      });
      setDecisionText("");
      await load();
    } catch (e) { setErr(String(e)); }
  }

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.5rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("page.gov.title")} — Convene Board Governance</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.25rem' }}>
            Board Packets • Live Quorum Verification • Agenda Roll-Call Voting • Minute Signing
          </p>
        </div>
        <button className="btn btn-secondary" onClick={load}>{t("page.gov.load")}</button>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />

      {err && (
        <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>
          {err}
        </div>
      )}

      {/* Main Layout: Left Session List, Right Convene Live Session Room */}
      <div style={{ display: 'grid', gridTemplateColumns: '320px 1fr', gap: '1.5rem', alignItems: 'start' }}>
        
        {/* Left Column: Committees & Meetings List */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem' }}>
          
          {/* Scheduled Meetings */}
          <div className="glass-card">
            <h3 style={{ margin: '0 0 1rem 0', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <span>{t("page.gov.meet")}</span>
              <span className="badge">{meetings.length}</span>
            </h3>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', maxHeight: '350px', overflowY: 'auto' }}>
              {meetings.length === 0 ? (
                <p style={{ opacity: 0.6, fontSize: '0.9rem' }}>No meetings scheduled.</p>
              ) : (
                meetings.map((m) => {
                  const isSelected = m.id === activeMeetingId;
                  return (
                    <div
                      key={m.id}
                      onClick={() => selectMeeting(m.id)}
                      style={{
                        padding: '0.75rem 1rem',
                        borderRadius: '0.5rem',
                        background: isSelected ? 'rgba(59, 130, 246, 0.15)' : 'var(--card-bg)',
                        border: isSelected ? '1px solid var(--primary)' : '1px solid var(--border)',
                        cursor: 'pointer',
                        transition: 'all 0.2s ease'
                      }}
                    >
                      <div style={{ fontWeight: 600, fontSize: '0.95rem' }}>{m.title}</div>
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '0.4rem', fontSize: '0.8rem' }}>
                        <span className="badge">[{m.status}]</span>
                        <span style={{ opacity: 0.5 }}>{new Date(m.startsAt).toLocaleDateString()}</span>
                      </div>
                    </div>
                  );
                })
              )}
            </div>

            {/* Schedule New Meeting */}
            <div style={{ marginTop: '1.25rem', paddingTop: '1rem', borderTop: '1px solid var(--border)', display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
              <input
                className="input"
                placeholder="Committee ID"
                value={committeeId}
                onChange={(e) => setCommitteeId(e.target.value)}
              />
              <input
                className="input"
                placeholder="Meeting Title"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
              />
              <input
                className="input"
                placeholder="Initial Agenda Item"
                value={agendaTitle}
                onChange={(e) => setAgendaTitle(e.target.value)}
              />
              <button className="btn" onClick={schedule}>{t("page.gov.schedule")}</button>
            </div>
          </div>

          {/* Committee Management */}
          <div className="glass-card">
            <h3 style={{ margin: '0 0 1rem 0' }}>{t("page.gov.com")}</h3>
            <ul style={{ margin: '0 0 1rem 0', paddingLeft: '1.2rem', color: 'var(--foreground)', fontSize: '0.9rem' }}>
              {committees.map((c) => (
                <li key={c.id} style={{ marginBottom: '0.4rem' }}>
                  <b>{c.code}</b>: {c.name}
                </li>
              ))}
            </ul>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <input className="input" placeholder="Code" value={code} onChange={(e) => setCode(e.target.value)} />
              <button className="btn" onClick={createCommittee}>{t("page.gov.create")}</button>
            </div>
          </div>
        </div>

        {/* Right Column: Convene Board Room Session */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem' }}>
          {meetingDetail ? (
            <>
              {/* Meeting Header & Quorum Dashboard */}
              <div className="glass-card">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: '1rem' }}>
                  <div>
                    <h2 style={{ margin: '0 0 0.5rem 0' }}>{meetingDetail.meeting.title}</h2>
                    <div style={{ display: 'flex', gap: '1rem', opacity: 0.8, fontSize: '0.85rem' }}>
                      <span>Starts: {new Date(meetingDetail.meeting.startsAt).toLocaleString()}</span>
                      <span>Status: <b className="badge">[{meetingDetail.meeting.status}]</b></span>
                    </div>
                    <div style={{ marginTop: '0.75rem' }}>
                      <button className="btn btn-secondary" onClick={openBoardPacket} style={{ fontSize: '0.85rem' }}>
                        📜 View Convene Board Packet Dossier
                      </button>
                    </div>
                  </div>

                  {/* Quorum Badge (Convene Pattern) */}
                  {quorum && (
                    <div
                      style={{
                        padding: '0.75rem 1.25rem',
                        borderRadius: '0.75rem',
                        background: quorum.hasQuorum ? 'rgba(34, 197, 94, 0.15)' : 'rgba(234, 179, 8, 0.15)',
                        border: `1px solid ${quorum.hasQuorum ? '#22c55e' : '#eab308'}`,
                        textAlign: 'right'
                      }}
                    >
                      <div style={{ fontSize: '1.1rem', fontWeight: 700, color: quorum.hasQuorum ? '#22c55e' : '#eab308' }}>
                        {quorum.hasQuorum ? '✅ Quorum Achieved' : '⚠️ Quorum Pending'}
                      </div>
                      <div style={{ fontSize: '0.8rem', opacity: 0.8, marginTop: '0.2rem' }}>
                        {quorum.presentCount} of {quorum.totalMembers} present (Req: {quorum.quorumRequired})
                      </div>
                    </div>
                  )}
                </div>

                {/* Quorum Breakdown Stats */}
                {quorum && (
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: '0.75rem', marginTop: '1rem', padding: '0.75rem', background: 'var(--background)', borderRadius: '0.5rem' }}>
                    <div style={{ textAlign: 'center' }}>
                      <div style={{ fontSize: '0.75rem', opacity: 0.6 }}>Total Members</div>
                      <div style={{ fontSize: '1.2rem', fontWeight: 700 }}>{quorum.totalMembers}</div>
                    </div>
                    <div style={{ textAlign: 'center' }}>
                      <div style={{ fontSize: '0.75rem', color: '#22c55e' }}>Present</div>
                      <div style={{ fontSize: '1.2rem', fontWeight: 700, color: '#22c55e' }}>{quorum.presentCount}</div>
                    </div>
                    <div style={{ textAlign: 'center' }}>
                      <div style={{ fontSize: '0.75rem', color: '#ef4444' }}>Absent</div>
                      <div style={{ fontSize: '1.2rem', fontWeight: 700, color: '#ef4444' }}>{quorum.absentCount}</div>
                    </div>
                    <div style={{ textAlign: 'center' }}>
                      <div style={{ fontSize: '0.75rem', color: '#eab308' }}>Excused</div>
                      <div style={{ fontSize: '1.2rem', fontWeight: 700, color: '#eab308' }}>{quorum.excusedCount}</div>
                    </div>
                  </div>
                )}
              </div>

              {/* Agenda Packet & Live Voting Session */}
              <div className="glass-card">
                <h3 style={{ margin: '0 0 1rem 0' }}>📋 Agenda Packet & In-Meeting Voting (Azeus Convene)</h3>
                
                {meetingDetail.agenda.length === 0 ? (
                  <p style={{ opacity: 0.6 }}>No agenda items configured for this session.</p>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '1rem' }}>
                    {meetingDetail.agenda.map((item) => {
                      const isSelected = item.id === selectedAgendaId;
                      const tally = tallies.find((t) => t.agendaItemId === item.id) || { inFavor: 0, against: 0, abstain: 0, totalVotes: 0 };
                      return (
                        <div
                          key={item.id}
                          onClick={() => setSelectedAgendaId(item.id)}
                          style={{
                            padding: '1rem',
                            borderRadius: '0.5rem',
                            border: isSelected ? '1px solid var(--primary)' : '1px solid var(--border)',
                            background: isSelected ? 'rgba(59, 130, 246, 0.05)' : 'var(--card-bg)',
                            cursor: 'pointer'
                          }}
                        >
                          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                            <div style={{ fontWeight: 600 }}>
                              <span style={{ color: 'var(--primary)', marginRight: '0.5rem' }}>#{item.order}</span>
                              {item.title}
                            </div>
                            <div style={{ fontSize: '0.8rem', display: 'flex', gap: '0.75rem' }}>
                              <span style={{ color: '#22c55e' }}>👍 {tally.inFavor}</span>
                              <span style={{ color: '#ef4444' }}>👎 {tally.against}</span>
                              <span style={{ opacity: 0.7 }}>⚪ {tally.abstain}</span>
                            </div>
                          </div>

                          {item.description && (
                            <div style={{ fontSize: '0.85rem', opacity: 0.7, marginTop: '0.4rem' }}>
                              {item.description}
                            </div>
                          )}

                          {/* Live Voting Controls for Active Item */}
                          {isSelected && (
                            <div style={{ marginTop: '1rem', paddingTop: '1rem', borderTop: '1px solid var(--border)' }}>
                              <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', marginBottom: '0.75rem' }}>
                                <input
                                  className="input"
                                  placeholder="Voter Person ID"
                                  value={votingPersonId}
                                  onChange={(e) => setVotingPersonId(e.target.value)}
                                  style={{ maxWidth: '280px' }}
                                />
                                <input
                                  className="input"
                                  placeholder="Optional Vote Remarks / Justification"
                                  value={voteRemarks}
                                  onChange={(e) => setVoteRemarks(e.target.value)}
                                />
                              </div>
                              <div style={{ display: 'flex', gap: '0.75rem' }}>
                                <button
                                  className="btn"
                                  style={{ background: '#22c55e', color: 'white' }}
                                  onClick={(e) => { e.stopPropagation(); castVote("InFavor"); }}
                                >
                                  👍 Vote In Favor
                                </button>
                                <button
                                  className="btn"
                                  style={{ background: '#ef4444', color: 'white' }}
                                  onClick={(e) => { e.stopPropagation(); castVote("Against"); }}
                                >
                                  👎 Vote Against
                                </button>
                                <button
                                  className="btn btn-secondary"
                                  onClick={(e) => { e.stopPropagation(); castVote("Abstain"); }}
                                >
                                  ⚪ Abstain
                                </button>
                              </div>
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>

              {/* Adopted Decisions & Resolutions */}
              <div className="glass-card">
                <h3 style={{ margin: '0 0 1rem 0' }}>⚖️ Meeting Decisions & Resolutions</h3>
                <ul style={{ margin: '0 0 1rem 0', paddingLeft: '1.2rem', color: 'var(--foreground)' }}>
                  {decisions
                    .filter((d) => d.meetingId === activeMeetingId)
                    .map((d) => (
                      <li key={d.id} style={{ marginBottom: '0.5rem' }}>
                        {d.text} <span className="badge">[{d.status}]</span>
                      </li>
                    ))}
                </ul>
                <div style={{ display: 'flex', gap: '0.5rem' }}>
                  <input
                    className="input"
                    placeholder="Official Resolution / Decision text"
                    value={decisionText}
                    onChange={(e) => setDecisionText(e.target.value)}
                  />
                  <button className="btn" onClick={publishDecision}>Publish Resolution</button>
                </div>
              </div>

              {/* Conclude Session & Minutes */}
              <div className="glass-card">
                <h3 style={{ margin: '0 0 1rem 0' }}>📝 Conclude Meeting & Certify Minutes</h3>
                {meetingDetail.meeting.minutes ? (
                  <div style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1rem' }}>
                    <b>Certified Minutes:</b>
                    <p style={{ margin: '0.5rem 0 0 0', opacity: 0.8 }}>{meetingDetail.meeting.minutes}</p>
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
                    <textarea
                      className="input"
                      rows={3}
                      placeholder="Meeting minutes summary..."
                      value={minutesText}
                      onChange={(e) => setMinutesText(e.target.value)}
                    />
                    <button className="btn btn-secondary" onClick={concludeMeeting}>
                      {t("page.gov.conclude")}
                    </button>
                  </div>
                )}
              </div>

              {/* Board Room Chatter & Discussion */}
              <div className="glass-card">
                <Chatter
                  tenantId={tenantId || "11111111-1111-1111-1111-111111111111"}
                  entityType="Meeting"
                  entityId={meetingDetail.meeting.id}
                  currentPersonId={votingPersonId || "00000000-0000-0000-0000-000000000001"}
                />
              </div>
            </>
          ) : (
            <div className="glass-card" style={{ textAlign: 'center', padding: '3rem' }}>
              <p style={{ opacity: 0.7 }}>Select a meeting session from the left to view the Board Room.</p>
            </div>
          )}
        </div>

      </div>

      {packetModalOpen && packetData && (
        <div style={{
          position: 'fixed', top: 0, left: 0, right: 0, bottom: 0,
          background: 'rgba(0,0,0,0.75)', backdropFilter: 'blur(6px)',
          display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 9999, padding: '1rem'
        }}>
          <div style={{
            background: 'var(--card-bg, #1a1e29)', border: '1px solid var(--border)',
            borderRadius: '1rem', width: '100%', maxWidth: '800px', maxHeight: '90vh',
            overflowY: 'auto', padding: '2rem', boxShadow: '0 25px 50px -12px rgba(0,0,0,0.5)'
          }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', borderBottom: '1px solid var(--border)', paddingBottom: '1rem', marginBottom: '1.5rem' }}>
              <div>
                <span className="badge" style={{ background: 'rgba(59, 130, 246, 0.2)', color: '#60a5fa', marginBottom: '0.5rem', display: 'inline-block' }}>
                  Azeus Convene Executive Board Dossier
                </span>
                <h2 style={{ margin: 0 }}>{packetData.meeting.title}</h2>
                <p style={{ margin: '0.25rem 0 0 0', opacity: 0.7, fontSize: '0.85rem' }}>
                  Committee: <b>{packetData.committee?.name ?? "Board"}</b> • Convened: {new Date(packetData.meeting.startsAt).toLocaleString()}
                </p>
              </div>
              <button className="btn btn-secondary" onClick={() => setPacketModalOpen(false)}>✕ Close</button>
            </div>

            {/* Governance & Quorum Certification */}
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '1rem', marginBottom: '1.5rem' }}>
              <div style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                <div style={{ fontSize: '0.75rem', opacity: 0.7 }}>Quorum Status</div>
                <div style={{ fontWeight: 700, fontSize: '1.1rem', color: packetData.governance.hasQuorum ? '#22c55e' : '#eab308', marginTop: '0.25rem' }}>
                  {packetData.governance.hasQuorum ? '✅ Certified Legal Quorum' : '⚠️ Lacking Quorum'}
                </div>
                <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.25rem' }}>
                  {packetData.governance.presentCount} of {packetData.governance.totalMembers} present
                </div>
              </div>

              <div style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                <div style={{ fontSize: '0.75rem', opacity: 0.7 }}>Minutes Certification</div>
                <div style={{ fontWeight: 700, fontSize: '1.1rem', color: packetData.certification.isConcluded ? '#10b981' : '#60a5fa', marginTop: '0.25rem' }}>
                  {packetData.certification.isConcluded ? '🔏 Concluded & Certified' : '⏳ In Session'}
                </div>
                <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.25rem' }}>
                  {packetData.certification.hasMinutes ? 'Minutes filed in registry' : 'Draft pending conclusion'}
                </div>
              </div>

              <div style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                <div style={{ fontSize: '0.75rem', opacity: 0.7 }}>Dossier Generation</div>
                <div style={{ fontWeight: 700, fontSize: '1.1rem', marginTop: '0.25rem' }}>Convene V2</div>
                <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.25rem' }}>
                  {new Date(packetData.generatedAt).toLocaleTimeString()}
                </div>
              </div>
            </div>

            {/* Agenda & Balloting Summary */}
            <div style={{ marginBottom: '1.5rem' }}>
              <h3 style={{ margin: '0 0 0.75rem 0', fontSize: '1.1rem' }}>📋 Official Agenda & Voting Records</h3>
              <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
                {packetData.agenda.map((item: any, idx: number) => {
                  const tally = packetData.votingTallies.find((t: any) => t.agendaItemId === item.id) || { inFavor: 0, against: 0, abstain: 0, totalVotes: 0 };
                  return (
                    <div key={item.id} style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                        <b>#{item.order || (idx + 1)}. {item.title}</b>
                        <div style={{ display: 'flex', gap: '0.5rem', fontSize: '0.8rem' }}>
                          <span style={{ color: '#22c55e', background: 'rgba(34,197,94,0.1)', padding: '0.2rem 0.5rem', borderRadius: '0.25rem' }}>✓ {tally.inFavor} In Favor</span>
                          <span style={{ color: '#ef4444', background: 'rgba(239,68,68,0.1)', padding: '0.2rem 0.5rem', borderRadius: '0.25rem' }}>✗ {tally.against} Against</span>
                          <span style={{ opacity: 0.7, background: 'var(--card-bg)', padding: '0.2rem 0.5rem', borderRadius: '0.25rem' }}>— {tally.abstain} Abstain</span>
                        </div>
                      </div>
                      {item.description && <p style={{ margin: '0.5rem 0 0 0', opacity: 0.75, fontSize: '0.85rem' }}>{item.description}</p>}
                    </div>
                  );
                })}
              </div>
            </div>

            {/* Minutes & Decisions */}
            {packetData.certification.minutes && (
              <div style={{ marginBottom: '1.5rem' }}>
                <h3 style={{ margin: '0 0 0.5rem 0', fontSize: '1.1rem' }}>📝 Certified Session Minutes</h3>
                <pre style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)', whiteSpace: 'pre-wrap', fontSize: '0.85rem', lineHeight: 1.5, margin: 0 }}>
                  {packetData.certification.minutes}
                </pre>
              </div>
            )}

            {/* Decisions & Action Items */}
            {packetData.decisions.length > 0 && (
              <div style={{ marginBottom: '1.5rem' }}>
                <h3 style={{ margin: '0 0 0.5rem 0', fontSize: '1.1rem' }}>⚖️ Formal Decisions Adopted</h3>
                <ul style={{ margin: 0, paddingLeft: '1.2rem', fontSize: '0.9rem' }}>
                  {packetData.decisions.map((d: any) => <li key={d.id} style={{ marginBottom: '0.25rem' }}>{d.text} <span className="badge">[{d.status}]</span></li>)}
                </ul>
              </div>
            )}

            {/* Odoo-Style Universal Chatter & Next Activities */}
            {activeMeetingId && (
              <Chatter
                entityType="Meeting"
                entityId={activeMeetingId}
                tenantId={tenantId || "11111111-1111-1111-1111-111111111111"}
                currentPersonId="00000000-0000-0000-0000-000000000001"
              />
            )}
          </div>
        </div>
      )}
    </div>
  );
}
