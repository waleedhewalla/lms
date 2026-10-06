"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTenant } from "../../components/Ui";
import { useTranslation } from "../../components/TranslationProvider";
import { Chatter } from "../../components/Chatter";

type CorrType = "Memo" | "Letter" | "Circular" | "Decree";
type CorrStatus = "Draft" | "Pending Review" | "Approved" | "Published" | "Rejected";

type Corr = {
  id: string;
  number: string;
  type: CorrType;
  subject: string;
  content: string;
  status: CorrStatus;
  priority: string;
  authorId: string;
  reviewerId?: string;
  date: string;
};

type RoutingSlip = {
  id: string;
  correspondenceId: string;
  fromPersonId: string;
  toUnitId?: string;
  toPersonId?: string;
  actionRequired: string;
  instructions: string;
  dueAt?: string;
  createdAt: string;
  isCompleted: boolean;
  completedAt?: string;
};

const INITIAL_DATA: Corr[] = [
  { id: "1", number: "COR-2026-001", type: "Circular", subject: "Fall Semester Academic Guidelines", content: "Please review the updated academic guidelines for the upcoming Fall semester. Ensure all faculty members are aligned with the new assessment policies.", status: "Published", priority: "High", authorId: "Dean of Academic Affairs", date: "2026-09-01" },
  { id: "2", number: "COR-2026-002", type: "Memo", subject: "Budget Approval Delay Notice", content: "The Q3 budget approval process has been delayed by 2 weeks due to administrative audits.", status: "Pending Review", priority: "Normal", authorId: "Finance Dept", reviewerId: "Current User", date: "2026-09-18" },
  { id: "3", number: "COR-2026-003", type: "Letter", subject: "Invitation to Annual Symposium", content: "You are cordially invited to attend the Annual Science Symposium taking place next month.", status: "Draft", priority: "Low", authorId: "Current User", date: "2026-09-19" },
];

export default function CorrespondencePage() {
  const { t } = useTranslation();
  const [tenantId, setTenantId] = useTenant();
  const [activeTab, setActiveTab] = useState<"inbox" | "drafts" | "review" | "sent">("inbox");
  const [items, setItems] = useState<Corr[]>(INITIAL_DATA);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  // Routing Slips state (Tashira / التأشيرات والإحالات)
  const [routingSlips, setRoutingSlips] = useState<RoutingSlip[]>([]);
  const [slipAction, setSlipAction] = useState("DraftOfficialReply");
  const [slipInstructions, setSlipInstructions] = useState("");
  const [slipDays, setSlipDays] = useState(5);

  // New Draft State
  const [isCreating, setIsCreating] = useState(false);
  const [newType, setNewType] = useState<CorrType>("Memo");
  const [newSubject, setNewSubject] = useState("");
  const [newContent, setNewContent] = useState("");
  const [newPriority, setNewPriority] = useState("Normal");
  const [newReviewer, setNewReviewer] = useState("");

  const filteredItems = items.filter(item => {
    if (activeTab === "inbox") return item.status === "Published" || item.status === "Approved";
    if (activeTab === "drafts") return item.status === "Draft" && item.authorId === "Current User";
    if (activeTab === "review") return item.status === "Pending Review" && item.reviewerId === "Current User";
    if (activeTab === "sent") return item.authorId === "Current User" && item.status !== "Draft";
    return true;
  });

  const selectedItem = items.find(i => i.id === selectedId);

  const handleCreate = () => {
    const newItem: Corr = {
      id: Math.random().toString(36).substr(2, 9),
      number: `COR-2026-00${items.length + 1}`,
      type: newType,
      subject: newSubject,
      content: newContent,
      status: "Draft",
      priority: newPriority,
      authorId: "Current User",
      reviewerId: newReviewer,
      date: new Date().toISOString().split("T")[0]
    };
    setItems([...items, newItem]);
    setIsCreating(false);
    setNewSubject("");
    setNewContent("");
    setActiveTab("drafts");
    setSelectedId(newItem.id);
  };

  const updateStatus = (id: string, newStatus: CorrStatus) => {
    setItems(items.map(item => item.id === id ? { ...item, status: newStatus } : item));
    setSelectedId(null); // Deselect after action
  };

  async function loadRoutingSlips(id: string) {
    if (!tenantId) return;
    try {
      const list = await api<RoutingSlip[]>(`/api/correspondence/${id}/routing-slips?tenantId=${tenantId}`);
      setRoutingSlips(list);
    } catch {
      setRoutingSlips([]);
    }
  }

  async function appendDirective() {
    if (!selectedId || !slipInstructions.trim()) return;
    try {
      const dueAt = new Date(Date.now() + slipDays * 86400000).toISOString();
      if (tenantId) {
        await api(`/api/correspondence/${selectedId}/routing-slips`, {
          method: "POST",
          body: JSON.stringify({
            tenantId,
            fromPersonId: "00000000-0000-0000-0000-000000000001",
            toPersonId: "00000000-0000-0000-0000-000000000002",
            actionRequired: slipAction,
            instructions: slipInstructions.trim(),
            dueAt
          })
        });
        await loadRoutingSlips(selectedId);
      } else {
        const newSlip: RoutingSlip = {
          id: Math.random().toString(36).substr(2, 9),
          correspondenceId: selectedId,
          fromPersonId: "Executive Office",
          actionRequired: slipAction,
          instructions: slipInstructions.trim(),
          dueAt,
          createdAt: new Date().toISOString(),
          isCompleted: false
        };
        setRoutingSlips([newSlip, ...routingSlips]);
      }
      setSlipInstructions("");
    } catch (e) {
      console.error(e);
    }
  }

  async function completeDirective(slipId: string) {
    try {
      if (tenantId && selectedId) {
        await api(`/api/correspondence/${selectedId}/routing-slips/${slipId}/complete?tenantId=${tenantId}`, {
          method: "POST"
        });
      }
      setRoutingSlips(slips => slips.map(s => s.id === slipId ? { ...s, isCompleted: true, completedAt: new Date().toISOString() } : s));
    } catch (e) {
      console.error(e);
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', gap: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("page.cor.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>UX-COR-001 (Mocked Environment)</p>
        </div>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />

      <div style={{ display: 'flex', gap: '1.5rem', flex: 1, minHeight: '600px' }}>
        
        {/* Left Panel: Tabs and List */}
        <div className="glass-card" style={{ flex: '1', display: 'flex', flexDirection: 'column', padding: '1rem' }}>
          
          <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1rem', borderBottom: '1px solid var(--border)', paddingBottom: '1rem' }}>
            <button className={`btn ${activeTab === 'inbox' ? '' : 'btn-secondary'}`} style={{ flex: 1, padding: '0.5rem' }} onClick={() => { setActiveTab('inbox'); setIsCreating(false); setSelectedId(null); }}>{t("page.cor.tab.inbox")}</button>
            <button className={`btn ${activeTab === 'drafts' ? '' : 'btn-secondary'}`} style={{ flex: 1, padding: '0.5rem' }} onClick={() => { setActiveTab('drafts'); setIsCreating(false); setSelectedId(null); }}>{t("page.cor.tab.drafts")}</button>
            <button className={`btn ${activeTab === 'review' ? '' : 'btn-secondary'}`} style={{ flex: 1, padding: '0.5rem' }} onClick={() => { setActiveTab('review'); setIsCreating(false); setSelectedId(null); }}>{t("page.cor.tab.review")}</button>
            <button className={`btn ${activeTab === 'sent' ? '' : 'btn-secondary'}`} style={{ flex: 1, padding: '0.5rem' }} onClick={() => { setActiveTab('sent'); setIsCreating(false); setSelectedId(null); }}>{t("page.cor.tab.sent")}</button>
          </div>

          <div style={{ flex: 1, overflowY: 'auto' }}>
            {filteredItems.length === 0 ? (
              <p style={{ textAlign: 'center', opacity: 0.5, marginTop: '2rem' }}>{t("page.cor.empty")}</p>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                {filteredItems.map(item => (
                  <div 
                    key={item.id} 
                    onClick={() => { setSelectedId(item.id); setIsCreating(false); }}
                    style={{ 
                      padding: '1rem', 
                      borderRadius: '0.5rem', 
                      background: selectedId === item.id ? 'rgba(59, 130, 246, 0.1)' : 'var(--background)',
                      border: `1px solid ${selectedId === item.id ? 'var(--primary)' : 'transparent'}`,
                      cursor: 'pointer',
                      transition: 'all 0.2s'
                    }}
                  >
                    <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '0.5rem' }}>
                      <strong style={{ fontSize: '0.9rem' }}>{item.number}</strong>
                      <span className="badge" style={{ fontSize: '0.7rem' }}>{item.status}</span>
                    </div>
                    <div style={{ fontWeight: 600, marginBottom: '0.25rem' }}>{item.subject}</div>
                    <div style={{ fontSize: '0.85rem', opacity: 0.7, display: 'flex', justifyContent: 'space-between' }}>
                      <span>{item.authorId}</span>
                      <span>{item.date}</span>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>

          <button className="btn" style={{ marginTop: '1rem' }} onClick={() => { setIsCreating(true); setSelectedId(null); }}>
            + {t("page.cor.draft")}
          </button>
        </div>

        {/* Right Panel: Detail View or Compose */}
        <div className="glass-card" style={{ flex: '2', display: 'flex', flexDirection: 'column', padding: '2rem' }}>
          
          {isCreating ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem', height: '100%' }}>
              <h2 style={{ margin: 0 }}>{t("page.cor.draft")}</h2>
              
              <div style={{ display: 'flex', gap: '1rem' }}>
                <div style={{ flex: 1 }}>
                  <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: 600, fontSize: '0.9rem' }}>{t("page.cor.type")}</label>
                  <select className="input" value={newType} onChange={e => setNewType(e.target.value as CorrType)}>
                    <option value="Memo">{t("page.cor.type.memo")}</option>
                    <option value="Letter">{t("page.cor.type.letter")}</option>
                    <option value="Circular">{t("page.cor.type.circular")}</option>
                    <option value="Decree">{t("page.cor.type.decree")}</option>
                  </select>
                </div>
                <div style={{ flex: 1 }}>
                  <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: 600, fontSize: '0.9rem' }}>{t("page.cor.priority")}</label>
                  <select className="input" value={newPriority} onChange={e => setNewPriority(e.target.value)}>
                    <option value="Low">Low</option>
                    <option value="Normal">Normal</option>
                    <option value="High">High</option>
                  </select>
                </div>
              </div>

              <div>
                <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: 600, fontSize: '0.9rem' }}>{t("page.cor.sub")}</label>
                <input className="input" value={newSubject} onChange={e => setNewSubject(e.target.value)} placeholder={t("page.cor.sub")} />
              </div>

              <div>
                <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: 600, fontSize: '0.9rem' }}>{t("page.cor.rev")}</label>
                <input className="input" value={newReviewer} onChange={e => setNewReviewer(e.target.value)} placeholder="Reviewer ID or Name" />
              </div>

              <div style={{ flex: 1, display: 'flex', flexDirection: 'column' }}>
                <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: 600, fontSize: '0.9rem' }}>{t("page.cor.content")}</label>
                <textarea className="input" style={{ flex: 1, resize: 'none' }} value={newContent} onChange={e => setNewContent(e.target.value)} placeholder="Write correspondence content here..."></textarea>
              </div>

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '1rem' }}>
                <button className="btn btn-secondary" onClick={() => setIsCreating(false)}>Cancel</button>
                <button className="btn" onClick={handleCreate}>{t("page.cor.create")}</button>
              </div>
            </div>

          ) : selectedItem ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem', height: '100%' }}>
              
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: '1rem', marginBottom: '0.5rem' }}>
                    <h2 style={{ margin: 0 }}>{selectedItem.subject}</h2>
                    <span className="badge">{selectedItem.status}</span>
                  </div>
                  <div style={{ fontSize: '0.9rem', opacity: 0.7 }}>
                    {selectedItem.number} • {selectedItem.type} • Priority: {selectedItem.priority}
                  </div>
                </div>
                <div style={{ textAlign: 'right' }}>
                  <div style={{ fontWeight: 600 }}>{selectedItem.authorId}</div>
                  <div style={{ fontSize: '0.9rem', opacity: 0.7 }}>{selectedItem.date}</div>
                </div>
              </div>

              <div style={{ flex: 1, background: 'var(--background)', padding: '1.5rem', borderRadius: '0.5rem', border: '1px solid var(--border)', overflowY: 'auto', lineHeight: 1.6 }}>
                {selectedItem.content}
              </div>

              {/* Executive Routing Slips (Tashira / التأشيرات والتوجيهات الإدارية) */}
              <div style={{ background: 'rgba(59, 130, 246, 0.05)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid rgba(59, 130, 246, 0.2)' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                  <strong style={{ fontSize: '0.95rem' }}>📌 Executive Directives & Referral Slips (التأشيرات والإحالات)</strong>
                  <span className="badge" style={{ background: 'rgba(59, 130, 246, 0.2)', color: 'var(--primary)' }}>Official Routing</span>
                </div>
                <div style={{ fontSize: '0.85rem', opacity: 0.8, marginBottom: '0.75rem' }}>
                  Executive directives appended to this letter dispatching mandatory actions to academic/administrative units with compliance deadlines.
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.5rem', marginBottom: '0.5rem' }}>
                  <select className="input" style={{ fontSize: '0.85rem' }} defaultValue="DraftOfficialReply">
                    <option value="DraftOfficialReply">Draft Official Reply (إعداد الرد الرسمي)</option>
                    <option value="UrgentExecution">Urgent Execution (للتنفيذ العاجل)</option>
                    <option value="StudyAndAdvise">Study & Advise (للدراسة وإبداء الرأي)</option>
                    <option value="ForInformation">For Information & File (للعلم والحفظ)</option>
                  </select>
                  <input className="input" placeholder="Forward to Unit / Officer ID" style={{ fontSize: '0.85rem' }} />
                </div>
                <div style={{ display: 'flex', gap: '0.5rem' }}>
                  <input className="input" placeholder="Executive notes / instructions..." style={{ fontSize: '0.85rem', flex: 1 }} />
                  <button className="btn btn-secondary" style={{ fontSize: '0.85rem', whiteSpace: 'nowrap' }} onClick={() => alert("Directive registered and added to recipient's My Work queue!")}>
                    + Append Directive
                  </button>
                </div>
              </div>

              {/* Workflow Actions */}
              <div style={{ display: 'flex', gap: '1rem', justifyContent: 'flex-end', marginTop: 'auto' }}>
                
                {selectedItem.status === "Draft" && (
                  <button className="btn" onClick={() => updateStatus(selectedItem.id, "Pending Review")}>{t("page.cor.submit")}</button>
                )}

                {selectedItem.status === "Pending Review" && activeTab === "review" && (
                  <>
                    <button className="btn" style={{ background: '#ef4444' }} onClick={() => updateStatus(selectedItem.id, "Rejected")}>{t("page.cor.reject")}</button>
                    <button className="btn" style={{ background: '#10b981' }} onClick={() => updateStatus(selectedItem.id, "Approved")}>{t("page.cor.approve")}</button>
                  </>
                )}
                
              </div>

              {/* Odoo-Style Universal Chatter & Next Activities */}
              <Chatter
                entityType="Correspondence"
                entityId={selectedItem.id}
                tenantId={tenantId || "11111111-1111-1111-1111-111111111111"}
                currentPersonId="00000000-0000-0000-0000-000000000001"
              />
            </div>

          ) : (
            <div style={{ display: 'flex', flex: 1, alignItems: 'center', justifyContent: 'center', opacity: 0.5 }}>
              Select an item to view details
            </div>
          )}

        </div>
      </div>
    </div>
  );
}
