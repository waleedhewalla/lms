'use client';

import React, { useState, useEffect } from 'react';

export type ActivityType = 'ToDo' | 'Review' | 'Sign' | 'Call' | 'Meeting';

export interface ScheduledActivity {
  id: string;
  tenantId: string;
  entityType: string;
  entityId: string;
  type: ActivityType;
  assigneeId: string;
  summary: string;
  dueDate: string;
  isCompleted: boolean;
  completedAt?: string | null;
  createdAt: string;
}

export interface RecordComment {
  id: string;
  tenantId: string;
  entityType: string;
  entityId: string;
  authorId: string;
  content: string;
  isInternalOnly: boolean;
  createdAt: string;
}

export interface EntityFollower {
  id: string;
  tenantId: string;
  entityType: string;
  entityId: string;
  personId: string;
  createdAt: string;
}

interface ChatterProps {
  entityType: string;
  entityId: string;
  tenantId: string;
  currentPersonId: string;
  apiBase?: string;
  token?: string;
}

export function Chatter({
  entityType,
  entityId,
  tenantId,
  currentPersonId,
  apiBase = 'http://localhost:5000',
  token = '',
}: ChatterProps) {
  const [activeTab, setActiveTab] = useState<'message' | 'internal' | 'activity'>('message');
  const [comments, setComments] = useState<RecordComment[]>([]);
  const [activities, setActivities] = useState<ScheduledActivity[]>([]);
  const [followers, setFollowers] = useState<EntityFollower[]>([]);
  const [isFollowing, setIsFollowing] = useState<boolean>(false);
  const [loading, setLoading] = useState<boolean>(false);

  // Form states
  const [content, setContent] = useState<string>('');
  const [activityType, setActivityType] = useState<ActivityType>('Review');
  const [activitySummary, setActivitySummary] = useState<string>('');
  const [activityDueDate, setActivityDueDate] = useState<string>(
    new Date(Date.now() + 86400000 * 2).toISOString().slice(0, 10)
  );

  const authHeaders = {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
  };

  const loadData = async () => {
    if (!tenantId || !entityId) return;
    try {
      setLoading(true);
      const [commsRes, actsRes, folsRes] = await Promise.all([
        fetch(`${apiBase}/api/chatter/comments?tenantId=${tenantId}&entityType=${entityType}&entityId=${entityId}`, {
          headers: authHeaders,
        }),
        fetch(`${apiBase}/api/activities?tenantId=${tenantId}&entityType=${entityType}&entityId=${entityId}`, {
          headers: authHeaders,
        }),
        fetch(`${apiBase}/api/chatter/followers?tenantId=${tenantId}&entityType=${entityType}&entityId=${entityId}`, {
          headers: authHeaders,
        }),
      ]);

      if (commsRes.ok) {
        const commsData = await commsRes.json();
        setComments(Array.isArray(commsData) ? commsData : []);
      }
      if (actsRes.ok) {
        const actsData = await actsRes.json();
        setActivities(Array.isArray(actsData) ? actsData : []);
      }
      if (folsRes.ok) {
        const folsData = await folsRes.json();
        const list: EntityFollower[] = Array.isArray(folsData) ? folsData : [];
        setFollowers(list);
        setIsFollowing(list.some(f => f.personId === currentPersonId));
      }
    } catch (err) {
      console.error('Failed to load chatter data:', err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadData();
  }, [tenantId, entityId, entityType]);

  const handlePostComment = async () => {
    if (!content.trim()) return;
    try {
      const isInternal = activeTab === 'internal';
      const res = await fetch(`${apiBase}/api/chatter/comments`, {
        method: 'POST',
        headers: authHeaders,
        body: JSON.stringify({
          tenantId,
          entityType,
          entityId,
          authorId: currentPersonId,
          content: content.trim(),
          isInternalOnly: isInternal,
        }),
      });
      if (res.ok) {
        setContent('');
        await loadData();
      }
    } catch (err) {
      console.error('Failed to post comment:', err);
    }
  };

  const handleScheduleActivity = async () => {
    if (!activitySummary.trim()) return;
    try {
      const res = await fetch(`${apiBase}/api/activities`, {
        method: 'POST',
        headers: authHeaders,
        body: JSON.stringify({
          tenantId,
          entityType,
          entityId,
          type: activityType,
          assigneeId: currentPersonId,
          summary: activitySummary.trim(),
          dueDate: new Date(activityDueDate).toISOString(),
        }),
      });
      if (res.ok) {
        setActivitySummary('');
        setActiveTab('message');
        await loadData();
      }
    } catch (err) {
      console.error('Failed to schedule activity:', err);
    }
  };

  const handleCompleteActivity = async (activityId: string) => {
    try {
      const res = await fetch(`${apiBase}/api/activities/${activityId}/complete`, {
        method: 'PATCH',
        headers: authHeaders,
        body: JSON.stringify({ tenantId }),
      });
      if (res.ok) {
        await loadData();
      }
    } catch (err) {
      console.error('Failed to complete activity:', err);
    }
  };

  const handleToggleFollow = async () => {
    try {
      const res = await fetch(`${apiBase}/api/chatter/follow`, {
        method: 'POST',
        headers: authHeaders,
        body: JSON.stringify({
          tenantId,
          entityType,
          entityId,
          personId: currentPersonId,
        }),
      });
      if (res.ok) {
        await loadData();
      }
    } catch (err) {
      console.error('Failed to follow entity:', err);
    }
  };

  const getActivityBadgeColor = (type: ActivityType) => {
    switch (type) {
      case 'Sign': return '#8b5cf6';
      case 'Review': return '#3b82f6';
      case 'Call': return '#10b981';
      case 'Meeting': return '#f59e0b';
      case 'ToDo': default: return '#64748b';
    }
  };

  const isOverdue = (dateStr: string) => {
    return new Date(dateStr) < new Date();
  };

  return (
    <div style={{
      background: 'var(--surface)',
      backdropFilter: 'blur(12px)',
      borderRadius: '16px',
      border: '1px solid var(--border)',
      padding: '24px',
      marginTop: '28px',
      display: 'flex',
      flexDirection: 'column',
      gap: '20px',
      boxShadow: '0 8px 32px rgba(0, 0, 0, 0.2)',
    }}>
      {/* Header bar: Collaboration & Followers */}
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        paddingBottom: '16px',
        borderBottom: '1px solid var(--border)',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
          <span style={{ fontSize: '18px', fontWeight: 600, letterSpacing: '-0.02em' }}>
            💬 Collaboration & Chatter
          </span>
          <span style={{
            fontSize: '12px',
            padding: '3px 10px',
            borderRadius: '20px',
            background: 'rgba(59, 130, 246, 0.15)',
            color: 'var(--primary)',
            fontWeight: 500,
          }}>
            {entityType}
          </span>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
          <span style={{ fontSize: '13px', color: 'rgba(226, 232, 240, 0.7)' }}>
            👥 {followers.length} {followers.length === 1 ? 'follower' : 'followers'}
          </span>
          <button
            onClick={handleToggleFollow}
            style={{
              padding: '6px 14px',
              borderRadius: '8px',
              border: '1px solid var(--border)',
              background: isFollowing ? 'rgba(59, 130, 246, 0.2)' : 'var(--secondary)',
              color: isFollowing ? 'var(--primary)' : 'inherit',
              cursor: 'pointer',
              fontWeight: 500,
              fontSize: '13px',
              transition: 'all 0.2s ease',
            }}
          >
            {isFollowing ? '✓ Following' : '+ Follow'}
          </button>
        </div>
      </div>

      {/* Tabs */}
      <div style={{ display: 'flex', gap: '8px', borderBottom: '1px solid var(--border)', paddingBottom: '8px' }}>
        <button
          onClick={() => setActiveTab('message')}
          style={{
            padding: '8px 16px',
            borderRadius: '8px',
            border: 'none',
            background: activeTab === 'message' ? 'var(--primary)' : 'transparent',
            color: activeTab === 'message' ? '#fff' : 'inherit',
            fontWeight: 500,
            cursor: 'pointer',
            fontSize: '14px',
            transition: 'background 0.2s ease',
          }}
        >
          ✉️ Send Message
        </button>
        <button
          onClick={() => setActiveTab('internal')}
          style={{
            padding: '8px 16px',
            borderRadius: '8px',
            border: 'none',
            background: activeTab === 'internal' ? '#f59e0b' : 'transparent',
            color: activeTab === 'internal' ? '#000' : 'inherit',
            fontWeight: 500,
            cursor: 'pointer',
            fontSize: '14px',
            transition: 'background 0.2s ease',
          }}
        >
          🔒 Log Internal Note
        </button>
        <button
          onClick={() => setActiveTab('activity')}
          style={{
            padding: '8px 16px',
            borderRadius: '8px',
            border: 'none',
            background: activeTab === 'activity' ? 'var(--accent)' : 'transparent',
            color: activeTab === 'activity' ? '#fff' : 'inherit',
            fontWeight: 500,
            cursor: 'pointer',
            fontSize: '14px',
            transition: 'background 0.2s ease',
          }}
        >
          ⏱️ Schedule Next Activity
        </button>
      </div>

      {/* Tab Inputs */}
      {activeTab === 'activity' ? (
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          gap: '12px',
          background: 'rgba(0, 0, 0, 0.15)',
          padding: '16px',
          borderRadius: '12px',
          border: '1px solid var(--border)',
        }}>
          <div style={{ display: 'flex', gap: '12px', flexWrap: 'wrap' }}>
            <div style={{ flex: '1', minWidth: '150px' }}>
              <label style={{ fontSize: '12px', opacity: 0.8, display: 'block', marginBottom: '4px' }}>Activity Type</label>
              <select
                value={activityType}
                onChange={e => setActivityType(e.target.value as ActivityType)}
                style={{
                  width: '100%',
                  padding: '8px 12px',
                  borderRadius: '8px',
                  border: '1px solid var(--border)',
                  background: 'var(--secondary)',
                  color: 'inherit',
                }}
              >
                <option value="Review">Review Document</option>
                <option value="Sign">Signature / Seal</option>
                <option value="Call">Phone / Follow-up</option>
                <option value="Meeting">Schedule Meeting</option>
                <option value="ToDo">To-Do Action</option>
              </select>
            </div>
            <div style={{ flex: '1', minWidth: '150px' }}>
              <label style={{ fontSize: '12px', opacity: 0.8, display: 'block', marginBottom: '4px' }}>Due Date</label>
              <input
                type="date"
                value={activityDueDate}
                onChange={e => setActivityDueDate(e.target.value)}
                style={{
                  width: '100%',
                  padding: '8px 12px',
                  borderRadius: '8px',
                  border: '1px solid var(--border)',
                  background: 'var(--secondary)',
                  color: 'inherit',
                }}
              />
            </div>
          </div>
          <div>
            <label style={{ fontSize: '12px', opacity: 0.8, display: 'block', marginBottom: '4px' }}>Summary / Instructions</label>
            <input
              type="text"
              placeholder="e.g. Countersign academic appointment dossier"
              value={activitySummary}
              onChange={e => setActivitySummary(e.target.value)}
              style={{
                width: '100%',
                padding: '10px 12px',
                borderRadius: '8px',
                border: '1px solid var(--border)',
                background: 'var(--secondary)',
                color: 'inherit',
              }}
            />
          </div>
          <button
            onClick={handleScheduleActivity}
            style={{
              alignSelf: 'flex-end',
              padding: '8px 18px',
              borderRadius: '8px',
              border: 'none',
              background: 'var(--accent)',
              color: '#fff',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            Schedule Activity
          </button>
        </div>
      ) : (
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          gap: '10px',
          background: activeTab === 'internal' ? 'rgba(245, 158, 11, 0.08)' : 'rgba(0, 0, 0, 0.15)',
          padding: '16px',
          borderRadius: '12px',
          border: activeTab === 'internal' ? '1px solid rgba(245, 158, 11, 0.3)' : '1px solid var(--border)',
        }}>
          {activeTab === 'internal' && (
            <div style={{ fontSize: '12px', color: '#f59e0b', fontWeight: 500 }}>
              🔒 Internal Note: Visible only to faculty and staff reviewers.
            </div>
          )}
          <textarea
            rows={3}
            placeholder={activeTab === 'internal' ? 'Log an internal observation or policy check...' : 'Send an official communication or response...'}
            value={content}
            onChange={e => setContent(e.target.value)}
            style={{
              width: '100%',
              padding: '12px',
              borderRadius: '8px',
              border: '1px solid var(--border)',
              background: 'var(--secondary)',
              color: 'inherit',
              resize: 'vertical',
              fontSize: '14px',
              fontFamily: 'inherit',
            }}
          />
          <button
            onClick={handlePostComment}
            style={{
              alignSelf: 'flex-end',
              padding: '8px 18px',
              borderRadius: '8px',
              border: 'none',
              background: activeTab === 'internal' ? '#f59e0b' : 'var(--primary)',
              color: activeTab === 'internal' ? '#000' : '#fff',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            {activeTab === 'internal' ? 'Log Note' : 'Send Message'}
          </button>
        </div>
      )}

      {/* Planned Activities Section */}
      {activities.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
          <span style={{ fontSize: '14px', fontWeight: 600, opacity: 0.9 }}>
            ⏱️ Next Planned Activities ({activities.filter(a => !a.isCompleted).length} pending)
          </span>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
            {activities.map(act => {
              const overdue = !act.isCompleted && isOverdue(act.dueDate);
              return (
                <div
                  key={act.id}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'space-between',
                    padding: '12px 16px',
                    borderRadius: '10px',
                    background: act.isCompleted
                      ? 'rgba(255, 255, 255, 0.03)'
                      : overdue
                      ? 'rgba(239, 68, 68, 0.1)'
                      : 'rgba(59, 130, 246, 0.05)',
                    border: act.isCompleted
                      ? '1px solid rgba(255, 255, 255, 0.05)'
                      : overdue
                      ? '1px solid rgba(239, 68, 68, 0.3)'
                      : '1px solid var(--border)',
                    opacity: act.isCompleted ? 0.6 : 1,
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
                    <span style={{
                      padding: '4px 10px',
                      borderRadius: '6px',
                      fontSize: '12px',
                      fontWeight: 600,
                      background: getActivityBadgeColor(act.type),
                      color: '#fff',
                    }}>
                      {act.type}
                    </span>
                    <span style={{
                      fontSize: '14px',
                      textDecoration: act.isCompleted ? 'line-through' : 'none',
                    }}>
                      {act.summary}
                    </span>
                  </div>

                  <div style={{ display: 'flex', alignItems: 'center', gap: '14px' }}>
                    <span style={{
                      fontSize: '12px',
                      color: act.isCompleted
                        ? 'rgba(255, 255, 255, 0.5)'
                        : overdue
                        ? '#ef4444'
                        : 'rgba(226, 232, 240, 0.7)',
                      fontWeight: overdue ? 600 : 400,
                    }}>
                      {act.isCompleted ? '✓ Completed' : `Due: ${new Date(act.dueDate).toLocaleDateString()}`}
                    </span>
                    {!act.isCompleted && (
                      <button
                        onClick={() => handleCompleteActivity(act.id)}
                        style={{
                          padding: '4px 10px',
                          borderRadius: '6px',
                          border: '1px solid var(--border)',
                          background: 'var(--surface-hover)',
                          color: 'inherit',
                          cursor: 'pointer',
                          fontSize: '12px',
                        }}
                      >
                        ✓ Mark Done
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* Stream / Timeline of Comments & Notes */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '12px', marginTop: '8px' }}>
        <span style={{ fontSize: '14px', fontWeight: 600, opacity: 0.9 }}>
          📜 Timeline & Messages ({comments.length})
        </span>

        {comments.length === 0 ? (
          <div style={{
            padding: '24px',
            textAlign: 'center',
            fontSize: '13px',
            color: 'rgba(255, 255, 255, 0.4)',
            border: '1px dashed var(--border)',
            borderRadius: '10px',
          }}>
            No messages or internal notes yet. Use the tabs above to collaborate.
          </div>
        ) : (
          comments.map(c => (
            <div
              key={c.id}
              style={{
                display: 'flex',
                flexDirection: 'column',
                gap: '6px',
                padding: '14px 16px',
                borderRadius: '10px',
                background: c.isInternalOnly ? 'rgba(245, 158, 11, 0.08)' : 'rgba(0, 0, 0, 0.18)',
                border: c.isInternalOnly ? '1px solid rgba(245, 158, 11, 0.25)' : '1px solid var(--border)',
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                  {c.isInternalOnly && (
                    <span style={{
                      fontSize: '11px',
                      padding: '2px 8px',
                      borderRadius: '4px',
                      background: '#f59e0b',
                      color: '#000',
                      fontWeight: 600,
                    }}>
                      INTERNAL NOTE
                    </span>
                  )}
                  <span style={{ fontSize: '13px', fontWeight: 600 }}>
                    {c.isInternalOnly ? 'Staff Reviewer' : 'University Correspondent'}
                  </span>
                </div>
                <span style={{ fontSize: '11px', color: 'rgba(255, 255, 255, 0.45)' }}>
                  {new Date(c.createdAt).toLocaleString()}
                </span>
              </div>
              <div style={{ fontSize: '14px', lineHeight: '1.5', whiteSpace: 'pre-wrap', opacity: 0.95 }}>
                {c.content}
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
