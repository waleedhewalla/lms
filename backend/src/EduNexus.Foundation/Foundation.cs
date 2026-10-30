using System.Text.RegularExpressions;

namespace EduNexus.Foundation;

public sealed record Tenant(Guid Id, string Slug, string Name, bool IsActive, DateTimeOffset CreatedAt)
{
    private static readonly Regex SlugRx = new("^[a-z0-9-]{3,63}$", RegexOptions.Compiled);

    public static Tenant Create(string slug, string name)
    {
        if (string.IsNullOrWhiteSpace(slug) || !SlugRx.IsMatch(slug))
            throw new ArgumentException("Slug must match ^[a-z0-9-]{3,63}$.", nameof(slug));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        return new Tenant(Guid.NewGuid(), slug.Trim().ToLowerInvariant(), name.Trim(), true, DateTimeOffset.UtcNow);
    }
}

public sealed record Organization(Guid Id, Guid TenantId, string Code, string Name, DateTimeOffset CreatedAt)
{
    public static Organization Create(Guid tenantId, string code, string name)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        return new Organization(Guid.NewGuid(), tenantId, code.Trim(), name.Trim(), DateTimeOffset.UtcNow);
    }
}

public sealed record Campus(Guid Id, Guid OrganizationId, Guid TenantId, string Code, string Name);

public sealed record OrganizationalUnit(
    Guid Id, Guid TenantId, Guid? ParentId, string Code, string Name, bool IsActive,
    Guid? LeaderPersonId = null, Guid? DeputyPersonId = null)
{
    public static OrganizationalUnit Create(Guid tenantId, string code, string name, Guid? parentId = null, Guid? leaderPersonId = null, Guid? deputyPersonId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (parentId == Guid.Empty) throw new ArgumentException("Invalid parent.", nameof(parentId));
        return new OrganizationalUnit(Guid.NewGuid(), tenantId, parentId, code.Trim(), name.Trim(), true, leaderPersonId, deputyPersonId);
    }
}

public enum PersonType { Employee, Student, Other }

public sealed record Person(
    Guid Id, Guid TenantId, PersonType Type, string FullName, string? Email, bool IsActive, DateTimeOffset CreatedAt,
    Guid? DepartmentId = null, string? AcademicRank = null)
{
    public static Person Create(Guid tenantId, PersonType type, string fullName, string? email = null, Guid? departmentId = null, string? academicRank = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("Full name required.", nameof(fullName));
        return new Person(Guid.NewGuid(), tenantId, type, fullName.Trim(), email?.Trim(), true, DateTimeOffset.UtcNow, departmentId, academicRank);
    }
}

public sealed record Role(Guid Id, Guid TenantId, string Code, string Name, IReadOnlyList<string> Permissions)
{
    public static Role Create(Guid tenantId, string code, string name, IEnumerable<string> permissions)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        var perms = permissions?.Distinct().ToList() ?? new List<string>();
        return new Role(Guid.NewGuid(), tenantId, code.Trim(), name.Trim(), perms);
    }
}

public sealed record RoleAssignment(Guid Id, Guid TenantId, Guid PersonId, Guid RoleId, string Scope, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);

public sealed record AuthorityDelegation(Guid Id, Guid TenantId, Guid FromPersonId, Guid ToPersonId, string Scope, DateTimeOffset ExpiresAt);

public sealed record AuditEvent(Guid Id, Guid TenantId, string Action, string EntityType, string EntityId, Guid? ActorId, DateTimeOffset At, string? Details);

/// <summary>
/// Transactional outbox: written atomically with domain changes, relayed to
/// RabbitMQ by EventRelay. Platform-level table (no RLS) so the relay can read
/// all tenants' events; consumers isolate by the tenant_id header/claim.
/// Mutable class (not a record): the relay stamps DispatchedAt after publish.
/// </summary>
public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }

    public OutboxEvent() { }

    public OutboxEvent(Guid id, Guid tenantId, string eventType, string payload, DateTimeOffset occurredAt, DateTimeOffset? dispatchedAt)
    {
        Id = id;
        TenantId = tenantId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
        DispatchedAt = dispatchedAt;
    }
}

// ============================ R2 — Communication & Workflow ============================

public enum CorrespondenceType { Incoming, Outgoing, Internal }
public enum CorrespondencePriority { Normal, High, Urgent }
public enum CorrespondenceStatus { Draft, Submitted, InReview, Approved, Rejected, Archived }

public sealed record Correspondence(
    Guid Id, Guid TenantId, string Number, CorrespondenceType Type,
    string Subject, string Content, Guid AuthorId, CorrespondencePriority Priority,
    bool IsConfidential, CorrespondenceStatus Status, DateTimeOffset CreatedAt,
    Guid? ParentCorrespondenceId = null)
{
    public static Correspondence Create(Guid tenantId, CorrespondenceType type, string subject,
        string content, Guid authorId, CorrespondencePriority priority, bool isConfidential,
        Guid? parentCorrespondenceId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Subject required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content required.", nameof(content));
        // Number assigned by the store (unique per tenant). Placeholder replaced on insert.
        return new Correspondence(Guid.NewGuid(), tenantId, "", type, subject.Trim(), content,
            authorId, priority, isConfidential, CorrespondenceStatus.Draft, DateTimeOffset.UtcNow,
            parentCorrespondenceId);
    }
}

/// <summary>To = the addressee who must act; Cc = receives a copy for information.</summary>
public enum RecipientKind { To, Cc }

public sealed record Correspondent(Guid Id, Guid TenantId, Guid CorrespondenceId, Guid? PersonId, string DisplayName, bool IsExternal,
    RecipientKind Kind = RecipientKind.To);

public enum ApprovalStatus { Pending, Approved, Rejected, Cancelled, ChangesRequested }
public enum ApprovalPriority { Normal, Accelerated }

public sealed record Approval(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId,
    Guid AssigneeId, ApprovalStatus Status, ApprovalPriority Priority,
    DateTimeOffset DueAt, DateTimeOffset? DecidedAt, Guid? DecidedBy, string? Comment);

public enum WorkTaskStatus { Open, InProgress, Done, Breached, Verified }
public enum WorkTaskPriority { Low, Normal, High, Urgent }

public sealed record WorkTask(
    Guid Id, Guid TenantId, string Title, Guid AssigneeId, Guid? ApprovalId,
    WorkTaskStatus Status, DateTimeOffset DueAt, DateTimeOffset CreatedAt,
    string Description = "", WorkTaskPriority Priority = WorkTaskPriority.Normal, int Progress = 0);

public sealed record TaskEvidence(Guid Id, Guid TenantId, Guid TaskId, Guid UploadedBy, string ObjectKey, string FileName, DateTimeOffset At);
public sealed record TaskComment(Guid Id, Guid TenantId, Guid TaskId, Guid AuthorId, string Text, DateTimeOffset At);

public enum NotificationChannel { InApp, Email, Sms }
public enum NotificationStatus { Queued, Sent, Failed }
public enum NotificationPriority { FYI, Normal, Important, Urgent, Emergency }

public sealed record Notification(
    Guid Id, Guid TenantId, Guid PersonId, string Title, string Body,
    NotificationChannel Channel, NotificationStatus Status, DateTimeOffset CreatedAt,
    NotificationPriority Priority = NotificationPriority.Normal, DateTimeOffset? ReadAt = null);

public sealed record NotificationTemplate(
    Guid Id, Guid TenantId, string Code, NotificationChannel Channel,
    string Subject, string BodyTemplate, bool IsActive);

public sealed record NotificationReceipt(
    Guid Id, Guid TenantId, Guid NotificationId, NotificationChannel Channel,
    NotificationStatus Status, string? ProviderMessageId, DateTimeOffset At);

/// <summary>Per-tenant monotonic counters (correspondence numbers, request numbers…). Row-locked per transaction.</summary>
public sealed record TenantSequence(Guid TenantId, string Scope, long NextValue);

// ============================ R3 — Governance ============================

/// <summary>A governing body. <see cref="QuorumPercent"/>: null means a simple majority of current members; otherwise at least that share (e.g. 66.67).</summary>
public sealed record Committee(Guid Id, Guid TenantId, string Code, string Name, bool IsActive, double? QuorumPercent = null);

public sealed record CommitteeMember(Guid Id, Guid TenantId, Guid CommitteeId, Guid PersonId, string Role, DateTimeOffset JoinedAt,
    DateTimeOffset? TermEndsAt = null);

public enum MeetingStatus { Scheduled, InProgress, Concluded, Cancelled }

public sealed record Meeting(
    Guid Id, Guid TenantId, Guid CommitteeId, string Title,
    DateTimeOffset StartsAt, MeetingStatus Status, string? Minutes);

public sealed record AgendaItem(Guid Id, Guid TenantId, Guid MeetingId, int Order, string Title, string? Description);

public enum AttendanceStatus { Present, Absent, Excused }

public sealed record Attendance(Guid Id, Guid TenantId, Guid MeetingId, Guid PersonId, AttendanceStatus Status);

public enum DecisionStatus { Draft, Published, Implemented, Verified, Closed }

public sealed record Decision(
    Guid Id, Guid TenantId, Guid MeetingId, string Text,
    DecisionStatus Status, DateTimeOffset PublishedAt);

public enum DecisionActionStatus { Assigned, InProgress, Done, Verified }

public sealed record DecisionAction(
    Guid Id, Guid TenantId, Guid DecisionId, Guid AssigneeId, string Description,
    DecisionActionStatus Status, DateTimeOffset DueAt);

public enum PolicyStatus { Draft, Published, Retired, Review, LegalReview, Approval }

/// <summary>
/// An institutional policy. <see cref="AttestEveryMonths"/>: people re-acknowledge on that cycle (null = once per version).
/// <see cref="QuizPassPercent"/> applies when the policy has quiz questions.
/// </summary>
public sealed record Policy(Guid Id, Guid TenantId, string Code, string Title, string Content, PolicyStatus Status, int Version,
    DateTimeOffset? NextReviewAt = null, Guid? OwnerId = null, int? AttestEveryMonths = null, int QuizPassPercent = 80);

/// <summary>An attestation: the person read (and, when there is a quiz, understood) a given version of the policy.</summary>
public sealed record PolicyAcknowledgement(Guid Id, Guid TenantId, Guid PolicyId, Guid PersonId, DateTimeOffset At,
    int PolicyVersion = 1, int? QuizScore = null);

// ============================ R4 — Institutional Intelligence ============================

public enum DocumentStatus { Draft, Published, Archived }
public enum DocumentClassification { Public, Internal, Confidential, Restricted }

public sealed record Document(
    Guid Id, Guid TenantId, string Title, DocumentStatus Status, int CurrentVersion,
    DocumentClassification Classification = DocumentClassification.Internal,
    DateTimeOffset? RetainUntil = null);

public sealed record DocumentVersion(
    Guid Id, Guid TenantId, Guid DocumentId, int Version,
    string ObjectKey, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

public sealed record DocumentShare(
    Guid Id, Guid TenantId, Guid DocumentId, Guid PersonId,
    DateTimeOffset SharedAt, DateTimeOffset? ExpiresAt);

/// <summary>An accreditation standard. <see cref="Framework"/> groups standards (e.g. NCAAA-I, ABET-EAC, or Custom).</summary>
public sealed record Standard(Guid Id, Guid TenantId, string Code, string Title, string Framework = "Custom");

public sealed record Criterion(Guid Id, Guid TenantId, Guid StandardId, string Code, string Text);

public sealed record Evidence(
    Guid Id, Guid TenantId, Guid CriterionId, string EntityType, Guid EntityId, string Note);

public enum FindingSeverity { Observation, Minor, Major }

public sealed record Finding(
    Guid Id, Guid TenantId, Guid CriterionId, FindingSeverity Severity, string Text, bool IsClosed);

public enum CorrectiveActionStatus { Open, InProgress, Done, Verified }

public sealed record CorrectiveAction(
    Guid Id, Guid TenantId, Guid FindingId, Guid AssigneeId, string Description,
    CorrectiveActionStatus Status, DateTimeOffset DueAt);

public sealed record StrategicPlan(Guid Id, Guid TenantId, string Title, int YearFrom, int YearTo);

public sealed record Objective(Guid Id, Guid TenantId, Guid PlanId, string Code, string Text);

public sealed record Kpi(
    Guid Id, Guid TenantId, Guid ObjectiveId, string Name, double Target, double Current, string Unit,
    double? Baseline = null, bool LowerIsBetter = false);

/// <summary>One recorded KPI value for a period (e.g. "2026-Q3" or "1447"). Kept as history; the KPI holds the latest.</summary>
public sealed record KpiReading(
    Guid Id, Guid TenantId, Guid KpiId, double Value, string Period, string? Note, string RecordedBy, DateTimeOffset RecordedAt);

// ============================ R5 — AI & Ecosystem ============================

/// <summary>Mandatory audit log for every AI interaction (09 governance).</summary>
public sealed record AiInteraction(
    Guid Id, Guid TenantId, string Capability, string InputExcerpt, string OutputExcerpt,
    string Model, double? Confidence, Guid? RequestedBy, DateTimeOffset At);

public enum IntegrationDeliveryStatus { Delivered, Failed }

public sealed record IntegrationEndpoint(
    Guid Id, Guid TenantId, string EventType, string TargetUrl, string Secret, bool IsActive);

public sealed record IntegrationDelivery(
    Guid Id, Guid TenantId, Guid EndpointId, string EventType, string Payload,
    IntegrationDeliveryStatus Status, int Attempts, string? LastError, DateTimeOffset At);

// ============================ R0.1 Track A — Requests & Dynamic Forms ============================

public enum RequestCategory
{
    Academic, Administrative, HR, Finance, Procurement,
    IT, Facilities, StudentAffairs, Research, Quality, Other,
}

public enum RequestStatus { Draft, Submitted, InReview, ChangesRequested, Approved, Rejected, Closed }

public sealed record Request(
    Guid Id, Guid TenantId, string Number, RequestCategory Category, string Title,
    Guid? FormId, Guid SubmitterId, RequestStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt);

public enum FormFieldType
{
    Text, LongText, Number, Currency, Date, DateTime, Dropdown, MultiSelect,
    Radio, Checkbox, User, Department, Organization, Document, Attachment, Signature, Table,
}

/// <summary>Dynamic form definition. SchemaJson: [{key,label,type,required,options[],validation{},visibleWhen{}}].</summary>
public sealed record Form(
    Guid Id, Guid TenantId, string Code, string Name, RequestCategory Category,
    string SchemaJson, int Version, bool IsActive);

public sealed record FormSubmission(
    Guid Id, Guid TenantId, Guid RequestId, Guid FormId, int FormVersion,
    string DataJson, Guid SubmittedBy, DateTimeOffset SubmittedAt);

// ============================ R0.1 Track B — Workflow Engine ============================

public enum WorkflowInstanceStatus { Running, Completed, Closed, Rejected }

/// <summary>
/// Versioned node graph. v1 linear nodes: start, approval {personId?, roleCode?, slaDays?},
/// task {title, personId?|assigneeFrom}, notification {personId?, text}, end.
/// v2 (parallel/conditional/timer) extends NodesJson without schema change.
/// </summary>
public sealed record WorkflowDefinition(
    Guid Id, Guid TenantId, string Code, string Name, int Version,
    string NodesJson, bool IsActive);

public sealed record WorkflowInstance(
    Guid Id, Guid TenantId, Guid DefinitionId, string EntityType, Guid EntityId,
    string CurrentNodeId, WorkflowInstanceStatus Status, string ContextJson, DateTimeOffset UpdatedAt);

// ============================ R0.1 Tracks C+D — Communications, Inbox, My Work ============================

public enum CommunicationKind { Announcement, Circular, Directive }
public enum CommunicationStatus { Draft, Published, Archived }

public sealed record Communication(
    Guid Id, Guid TenantId, CommunicationKind Kind, string Title, string Body,
    Guid AuthorId, bool RequiresAction, DateTimeOffset? DueAt,
    CommunicationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record CommunicationRecipient(Guid Id, Guid TenantId, Guid CommunicationId, Guid PersonId);

// ============================ R0.2 Odoo-Inspired Foundations: Chatter, Activities, Workspaces ============================

public enum ActivityType { ToDo, Review, Sign, Call, Meeting }

public sealed record ScheduledActivity(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId,
    ActivityType Type, Guid AssigneeId, string Summary,
    DateTimeOffset DueDate, bool IsCompleted, DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record RecordComment(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId,
    Guid AuthorId, string Content, bool IsInternalOnly, DateTimeOffset CreatedAt);

public sealed record EntityFollower(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId,
    Guid PersonId, DateTimeOffset CreatedAt);

public sealed record DocumentWorkspace(
    Guid Id, Guid TenantId, string Code, string Name, string? Description, bool IsActive);

public sealed record DocumentTag(
    Guid Id, Guid TenantId, Guid DocumentId, string TagCategory, string TagValue);

// ============================ R0.3 Enterprise Benchmarks: Azeus Convene (Governance), Folderit (ISO DMS), Odoo Correspondence ============================

public enum VoteChoice { InFavor, Against, Abstain }

public sealed record MeetingVote(
    Guid Id, Guid TenantId, Guid MeetingId, Guid AgendaItemId,
    Guid PersonId, VoteChoice Choice, DateTimeOffset CastAt, string? Remarks = null, string? RecordedBy = null);

public enum DispositionAction { Archive, PermanentPreservation, ReviewRequired, Disposal }

public sealed record DocumentRetentionPolicy(
    Guid Id, Guid TenantId, Guid DocumentId, string Standard,
    int RetentionPeriodMonths, DispositionAction DispositionAction,
    int ReviewIntervalMonths, DateTimeOffset? LastReviewedAt,
    DateTimeOffset NextReviewDueAt, Guid? ReviewedByPersonId = null, string? Notes = null);

public sealed record CorrespondenceRoutingSlip(
    Guid Id, Guid TenantId, Guid CorrespondenceId, Guid FromPersonId,
    Guid? ToUnitId, Guid? ToPersonId, string ActionRequired, string Instructions,
    DateTimeOffset? DueAt, DateTimeOffset CreatedAt, bool IsCompleted = false,
    DateTimeOffset? CompletedAt = null);

public sealed record DocumentActionRule(
    Guid Id, Guid TenantId, string TriggerCategory, string TriggerValue,
    string ActionType, string? TargetValue = null, bool IsActive = true);



// ============================ R0.2 Wave C — Governance depth ============================

public enum CalendarEventKind { Meeting, Deadline, PolicyReview, Exam, Training, Holiday, Other }

/// <summary>Institutional calendar entry (BBP §4.15). Meetings and reviews are also projected into the calendar feed.</summary>
public sealed record CalendarEvent(
    Guid Id, Guid TenantId, string Title, CalendarEventKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Location, string? SourceType, Guid? SourceId, Guid? OwnerId, DateTimeOffset CreatedAt);

public enum MinutesStatus { Draft, Submitted, Approved }

/// <summary>Versioned meeting minutes (BBP §8.14): each save is a new version; one version gets approved.</summary>
public sealed record MeetingMinutes(
    Guid Id, Guid TenantId, Guid MeetingId, int Version, string Content, MinutesStatus Status,
    Guid AuthorId, DateTimeOffset CreatedAt, Guid? ApprovedBy = null, DateTimeOffset? ApprovedAt = null);

/// <summary>Immutable snapshot of a policy revision (BBP §8.16).</summary>
public sealed record PolicyVersion(
    Guid Id, Guid TenantId, Guid PolicyId, int Version, string Title, string Content,
    string? ChangeNote, Guid? CreatedBy, DateTimeOffset CreatedAt);

public sealed record Procedure(Guid Id, Guid TenantId, Guid? PolicyId, string Code, string Title, string Steps, int Version, bool IsActive);

public sealed record DecisionActionEvidence(
    Guid Id, Guid TenantId, Guid ActionId, string ObjectKey, string FileName, string? Note, Guid UploadedBy, DateTimeOffset At);

/// <summary>Default SLA for an entity type (e.g. "Request"): hours to respond before the item is overdue.</summary>
public sealed record SlaPolicy(Guid Id, Guid TenantId, string EntityType, int ResponseHours, int EscalateAfterHours, bool IsActive);

/// <summary>A person's delivery preference for one channel (BBP §10.3).</summary>
public sealed record NotificationPreference(
    Guid Id, Guid TenantId, Guid PersonId, NotificationChannel Channel, bool Enabled,
    NotificationPriority MinPriority, int? QuietFromHour, int? QuietToHour)
{
    /// <summary>
    /// Whether a notification may go out on this channel. In-app and Emergency always go out; otherwise the
    /// channel must be enabled, the priority at or above the minimum, and (below Urgent) outside quiet hours (UTC).
    /// </summary>
    public static bool Allows(NotificationPreference? pref, NotificationChannel channel, NotificationPriority priority, DateTimeOffset now)
    {
        if (channel == NotificationChannel.InApp || priority == NotificationPriority.Emergency || pref is null) return true;
        if (!pref.Enabled || priority < pref.MinPriority) return false;
        if (priority >= NotificationPriority.Urgent || pref.QuietFromHour is not { } from || pref.QuietToHour is not { } to || from == to) return true;
        var h = now.UtcDateTime.Hour;
        var quiet = from < to ? h >= from && h < to : h >= from || h < to;
        return !quiet;
    }
}

// ---------------------------------------------------------------------------------------------------------
// Wave H — records retention & disposal (Saudi Law of Documents and Archives: classified records, retention
// periods, supervised disposal, transfer to the National Center for Archives and Records).
// ---------------------------------------------------------------------------------------------------------

public enum RecordClass { Administrative, Financial, Specialized }
public enum Disposition { Destroy, TransferToArchive, Permanent }

/// <summary>How long one kind of record is kept and what happens after. RetentionYears is null for Permanent.</summary>
public sealed record RetentionSchedule(
    Guid Id, Guid TenantId, string Code, string Name, string EntityType, RecordClass RecordClass,
    int? RetentionYears, Disposition Disposition, string? LegalReference, bool IsActive);

/// <summary>A legal/administrative hold: a held record is never disposed while the hold is active.</summary>
public sealed record RecordHold(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId, string Reason,
    string PlacedBy, DateTimeOffset PlacedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt)
{
    public bool IsActive => ReleasedAt is null;
}

public enum DisposalStatus { Proposed, Approved, Rejected, Executed, ExecutedWithErrors }

/// <summary>A proposed disposal run for one schedule. Approval must come from someone other than the proposer.</summary>
public sealed record DisposalBatch(
    Guid Id, Guid TenantId, Guid ScheduleId, Disposition Disposition, DisposalStatus Status,
    string ProposedBy, DateTimeOffset ProposedAt, string? DecidedBy, DateTimeOffset? DecidedAt,
    string? DecisionNote, DateTimeOffset? ExecutedAt);

public enum DisposalOutcome { Pending, Destroyed, Transferred, SkippedHold, StoragePending }

/// <summary>One record in a batch. Kept forever: together these rows are the disposal register.</summary>
public sealed record DisposalItem(
    Guid Id, Guid TenantId, Guid BatchId, string EntityType, Guid EntityId, string Reference, string Title,
    DateTimeOffset RecordDate, DateTimeOffset EligibleAt, DisposalOutcome Outcome,
    DateTimeOffset? ProcessedAt, string? Note);

public static class RetentionRules
{
    /// <summary>Record types a schedule can govern.</summary>
    public static readonly string[] EntityTypes = ["Correspondence", "Document"];

    /// <summary>
    /// When a record becomes eligible for disposal: its record date plus the retention period, but never
    /// before an explicit retain-until date. Null means never (permanent records).
    /// </summary>
    public static DateTimeOffset? EligibleAt(DateTimeOffset recordDate, int? retentionYears, Disposition disposition, DateTimeOffset? retainUntil = null)
    {
        if (disposition == Disposition.Permanent || retentionYears is null) return null;
        var byPeriod = recordDate.AddYears(retentionYears.Value);
        return retainUntil is { } until && until > byPeriod ? until : byPeriod;
    }

    /// <summary>Only finished correspondence may be disposed; drafts and items in flight are still working records.</summary>
    public static bool IsClosed(CorrespondenceStatus status) =>
        status is CorrespondenceStatus.Approved or CorrespondenceStatus.Rejected or CorrespondenceStatus.Archived;

    public static string? Validate(string code, string name, string entityType, int? retentionYears, Disposition disposition)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return "Code and name are required.";
        if (!EntityTypes.Contains(entityType)) return $"EntityType must be one of: {string.Join(", ", EntityTypes)}.";
        if (disposition == Disposition.Permanent && retentionYears is not null) return "Permanent records have no retention period.";
        if (disposition != Disposition.Permanent && retentionYears is not (>= 1 and <= 100)) return "RetentionYears must be 1–100.";
        return null;
    }
}

// ---------------------------------------------------------------------------------------------------------
// PDPL right to destruction (G2): erase a person's directory identity while keeping institutional records.
// ---------------------------------------------------------------------------------------------------------

public enum ErasureStatus { Proposed, Approved, Rejected, Executed }

/// <summary>
/// A request to erase/pseudonymise one person. Proposed and approved by different privacy officers.
/// Kept forever as the erasure register; it stores the pseudonym, never the erased name.
/// </summary>
public sealed record ErasureRequest(
    Guid Id, Guid TenantId, Guid PersonId, string Pseudonym, string Reason, ErasureStatus Status,
    string ProposedBy, DateTimeOffset ProposedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote,
    DateTimeOffset? ExecutedAt, string? Summary);

public static class Erasure
{
    /// <summary>Stable, non-identifying label that replaces the person's name everywhere.</summary>
    public static string PseudonymFor(Guid personId) => $"Former member {personId.ToString("N")[..8].ToUpperInvariant()}";

    /// <summary>The directory record after erasure: no name, no email, no rank, deactivated.</summary>
    public static Person Pseudonymise(Person p) =>
        p with { FullName = PseudonymFor(p.Id), Email = null, AcademicRank = null, IsActive = false };
}

// ============================ Wave I — Accreditation readiness & strategy scorecard ============================

public enum CriterionStatus { Met, Partial, NotMet }
public enum KpiStatus { Achieved, OnTrack, AtRisk, OffTrack }

public static class Accreditation
{
    /// <summary>
    /// Readiness of one criterion: no evidence is NotMet; an open Major finding caps it at Partial,
    /// as does an open Minor one; evidence with only observations (or none open) is Met.
    /// </summary>
    public static CriterionStatus Assess(int evidenceCount, int openMajor, int openMinor) =>
        evidenceCount == 0 ? CriterionStatus.NotMet
        : openMajor > 0 || openMinor > 0 ? CriterionStatus.Partial
        : CriterionStatus.Met;

    /// <summary>Share of criteria Met, Partial counting half, as a whole percentage.</summary>
    public static int ReadinessPercent(IReadOnlyCollection<CriterionStatus> statuses) =>
        statuses.Count == 0 ? 0
        : (int)Math.Round(100.0 * statuses.Sum(s => s switch { CriterionStatus.Met => 1.0, CriterionStatus.Partial => 0.5, _ => 0.0 }) / statuses.Count);
}

public static class KpiProgress
{
    /// <summary>Progress from baseline (default 0) to target, as a fraction; direction-aware. Null if the target equals the baseline.</summary>
    public static double? Fraction(Kpi k)
    {
        var from = k.Baseline ?? 0;
        var span = k.LowerIsBetter ? from - k.Target : k.Target - from;
        if (span == 0) return null;
        var moved = k.LowerIsBetter ? from - k.Current : k.Current - from;
        return moved / span;
    }

    /// <summary>Achieved at the target; on track from 75%; at risk from 50%; otherwise off track.</summary>
    public static KpiStatus Status(Kpi k)
    {
        var met = k.LowerIsBetter ? k.Current <= k.Target : k.Current >= k.Target;
        if (met) return KpiStatus.Achieved;
        var f = Fraction(k) ?? 0;
        return f >= 0.75 ? KpiStatus.OnTrack : f >= 0.5 ? KpiStatus.AtRisk : KpiStatus.OffTrack;
    }
}

/// <summary>A framework the institution can load into its standards library.</summary>
public sealed record FrameworkTemplate(string Code, string Name, string Body, IReadOnlyList<StandardTemplate> Standards);
public sealed record StandardTemplate(string Code, string Title, IReadOnlyList<(string Code, string Text)> Criteria);

/// <summary>
/// Built-in standards library. Standard titles follow the published frameworks; criteria are short
/// summaries for orientation. Institutions load the official sub-criteria text from their handbook
/// with the CSV import, which updates these rows in place by code.
/// </summary>
public static class AccreditationLibrary
{
    private static StandardTemplate S(string code, string title, params (string, string)[] criteria) => new(code, title, criteria);

    public static readonly IReadOnlyList<FrameworkTemplate> Frameworks =
    [
        new("NCAAA-I", "NCAAA Institutional Accreditation", "Education and Training Evaluation Commission (ETEC) — NCAAA",
        [
            S("NCAAA-I-1", "Mission, Vision and Strategic Planning", ("1.0", "Mission and vision are approved and guide a strategic plan that is implemented, monitored with KPIs and reviewed.")),
            S("NCAAA-I-2", "Governance, Leadership and Management", ("2.0", "Governance bodies, leadership, quality assurance, policies and integrity are effective and documented.")),
            S("NCAAA-I-3", "Teaching and Learning", ("3.0", "Learning outcomes, programme design, teaching quality and assessment are planned, measured and improved.")),
            S("NCAAA-I-4", "Students", ("4.0", "Admission, student records, support services and student activities are fair, effective and evaluated.")),
            S("NCAAA-I-5", "Faculty and Staff", ("5.0", "Recruitment, development, evaluation and retention of faculty and staff meet the institution's needs.")),
            S("NCAAA-I-6", "Institutional Resources", ("6.0", "Financial resources, facilities, learning resources and IT support the mission and are managed well.")),
            S("NCAAA-I-7", "Research and Innovation", ("7.0", "Research and innovation have a plan, support and outputs that are measured and reported.")),
            S("NCAAA-I-8", "Community Partnership", ("8.0", "Community partnership activities are planned, delivered and evaluated for impact.")),
        ]),
        new("NCAAA-P", "NCAAA Programmatic Accreditation", "Education and Training Evaluation Commission (ETEC) — NCAAA",
        [
            S("NCAAA-P-1", "Mission and Goals", ("1.0", "The programme mission and goals are consistent with the institution's and guide its operations.")),
            S("NCAAA-P-2", "Programme Management and Quality Assurance", ("2.0", "The programme is managed effectively with a working quality assurance system and KPIs.")),
            S("NCAAA-P-3", "Teaching and Learning", ("3.0", "Graduate attributes and learning outcomes are defined, taught, assessed and improved.")),
            S("NCAAA-P-4", "Students", ("4.0", "Admission, guidance, support and evaluation of students are effective.")),
            S("NCAAA-P-5", "Teaching Staff", ("5.0", "Teaching staff are qualified, sufficient, developed and evaluated.")),
            S("NCAAA-P-6", "Learning Resources, Facilities and Equipment", ("6.0", "Learning resources, facilities and equipment are adequate and evaluated.")),
        ]),
        new("ABET-EAC", "ABET Engineering (General Criteria)", "ABET Engineering Accreditation Commission",
        [
            S("ABET-1", "Students", ("1.0", "Student performance is evaluated and progress monitored; transfer and graduation policies are enforced.")),
            S("ABET-2", "Program Educational Objectives", ("2.0", "Published objectives consistent with the mission, reviewed periodically with constituencies.")),
            S("ABET-3", "Student Outcomes",
                ("3.1", "Identify, formulate and solve complex engineering problems."),
                ("3.2", "Apply engineering design to produce solutions that meet specified needs."),
                ("3.3", "Communicate effectively with a range of audiences."),
                ("3.4", "Recognise ethical and professional responsibilities and make informed judgements."),
                ("3.5", "Function effectively on a team."),
                ("3.6", "Develop and conduct experimentation, analyse and interpret data."),
                ("3.7", "Acquire and apply new knowledge using appropriate learning strategies.")),
            S("ABET-4", "Continuous Improvement", ("4.0", "Outcomes are regularly assessed and results are used to improve the programme.")),
            S("ABET-5", "Curriculum", ("5.0", "Curriculum covers mathematics, science, engineering topics and a culminating design experience.")),
            S("ABET-6", "Faculty", ("6.0", "Faculty are sufficient in number and competent to cover all curricular areas.")),
            S("ABET-7", "Facilities", ("7.0", "Classrooms, laboratories, equipment and computing are adequate for the outcomes.")),
            S("ABET-8", "Institutional Support", ("8.0", "Leadership, finances and staff are adequate to assure quality and continuity.")),
        ]),
    ];

    public static FrameworkTemplate? Find(string code) =>
        Frameworks.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Minimal RFC 4180 reader: commas, double-quoted fields with "" escapes, CRLF or LF line ends.</summary>
public static class CsvText
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]);
                row.Clear();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]);
        if (rows.Count > 0 && rows[0].Length > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }
}

// ============================ Wave J — Board experience ============================

/// <summary>A member's declared conflict of interest on one agenda item: they take no part in the vote.</summary>
public sealed record AgendaRecusal(
    Guid Id, Guid TenantId, Guid MeetingId, Guid AgendaItemId, Guid PersonId, string Reason, string DeclaredBy, DateTimeOffset DeclaredAt);

/// <summary>A periodic (usually annual) declaration of interests for the register.</summary>
public sealed record InterestDeclaration(
    Guid Id, Guid TenantId, Guid PersonId, string Period, bool NoInterests, string? Interests, DateTimeOffset DeclaredAt);

public enum ResolutionStatus { Open, Passed, Failed, Withdrawn, Expired }

/// <summary>A resolution decided in writing by circulation, without a meeting.</summary>
public sealed record CircularResolution(
    Guid Id, Guid TenantId, Guid CommitteeId, string Title, string Text, bool RequiresUnanimous, ResolutionStatus Status,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt, string? Outcome);

/// <summary>One member's response to a circular resolution: a vote, or a recusal (Choice null).</summary>
public sealed record ResolutionVote(
    Guid Id, Guid TenantId, Guid ResolutionId, Guid PersonId, VoteChoice? Choice, bool Recused, string? Remarks, DateTimeOffset CastAt);

public enum ItemOutcome { NoVotes, NoQuorum, Carried, Failed, Tied }

public static class BoardRules
{
    /// <summary>Members needed for quorum: a simple majority, or at least <paramref name="percent"/>% (rounded up).</summary>
    public static int QuorumRequired(int members, double? percent)
    {
        if (members <= 0) return 1;
        if (percent is not { } p) return members / 2 + 1;
        return Math.Clamp((int)Math.Ceiling(members * p / 100.0 - 0.01), 1, members);
    }

    /// <summary>An agenda item carries on more votes for than against (abstentions do not count), if the meeting is quorate for it.</summary>
    public static ItemOutcome Outcome(int inFavor, int against, int abstain, bool quorate)
    {
        if (inFavor + against + abstain == 0) return ItemOutcome.NoVotes;
        if (!quorate) return ItemOutcome.NoQuorum;
        return inFavor > against ? ItemOutcome.Carried : inFavor < against ? ItemOutcome.Failed : ItemOutcome.Tied;
    }

    /// <summary>
    /// A written resolution needs a majority of all eligible members (not of votes cast), or every eligible member when unanimous.
    /// It is decided as soon as the result can no longer change; otherwise it stays open.
    /// </summary>
    public static ResolutionStatus Decide(int eligible, int inFavor, int againstOrAbstain, bool unanimous, bool closed)
    {
        if (eligible <= 0) return closed ? ResolutionStatus.Expired : ResolutionStatus.Open;
        var needed = unanimous ? eligible : eligible / 2 + 1;
        if (inFavor >= needed) return ResolutionStatus.Passed;
        var pending = eligible - inFavor - againstOrAbstain;
        if (inFavor + pending < needed) return ResolutionStatus.Failed;
        return closed ? ResolutionStatus.Expired : ResolutionStatus.Open;
    }

    /// <summary>A member counts on a date unless their term had ended. (JoinedAt is when the row was entered, so it is not used.)</summary>
    public static bool IsCurrent(CommitteeMember m, DateTimeOffset at) => m.TermEndsAt is null || m.TermEndsAt > at;
}

/// <summary>A multiple-choice question checking that a policy was understood. Options are stored one per line.</summary>
public sealed record PolicyQuizQuestion(Guid Id, Guid TenantId, Guid PolicyId, int Order, string Question, string OptionsText, int CorrectIndex)
{
    public string[] Options => OptionsText.Split('\n');
}

public static class PolicyAttestation
{
    /// <summary>An attestation counts if it is for the current version and, with a cycle, recent enough.</summary>
    public static bool IsCurrent(PolicyAcknowledgement a, Policy p, DateTimeOffset now) =>
        a.PolicyVersion == p.Version && (p.AttestEveryMonths is not { } months || a.At > now.AddMonths(-months));

    /// <summary>When the person's attestation falls due again (null: never, or not attested).</summary>
    public static DateTimeOffset? DueAgain(PolicyAcknowledgement? a, Policy p) =>
        a is null || p.AttestEveryMonths is not { } months ? null : a.At.AddMonths(months);

    /// <summary>Percentage of questions answered correctly; unanswered counts as wrong.</summary>
    public static int Score(IReadOnlyList<PolicyQuizQuestion> questions, IReadOnlyList<int>? answers)
    {
        if (questions.Count == 0) return 100;
        var ordered = questions.OrderBy(q => q.Order).ToList();
        var right = ordered.Where((q, i) => answers is not null && i < answers.Count && answers[i] == q.CorrectIndex).Count();
        return (int)Math.Floor(100.0 * right / ordered.Count);
    }
}
