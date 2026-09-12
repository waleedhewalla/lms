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

public sealed record OrganizationalUnit(Guid Id, Guid TenantId, Guid? ParentId, string Code, string Name, bool IsActive)
{
    public static OrganizationalUnit Create(Guid tenantId, string code, string name, Guid? parentId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (parentId == Guid.Empty) throw new ArgumentException("Invalid parent.", nameof(parentId));
        return new OrganizationalUnit(Guid.NewGuid(), tenantId, parentId, code.Trim(), name.Trim(), true);
    }
}

public enum PersonType { Employee, Student, Other }

public sealed record Person(Guid Id, Guid TenantId, PersonType Type, string FullName, string? Email, bool IsActive, DateTimeOffset CreatedAt)
{
    public static Person Create(Guid tenantId, PersonType type, string fullName, string? email = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("Full name required.", nameof(fullName));
        return new Person(Guid.NewGuid(), tenantId, type, fullName.Trim(), email?.Trim(), true, DateTimeOffset.UtcNow);
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
    bool IsConfidential, CorrespondenceStatus Status, DateTimeOffset CreatedAt)
{
    public static Correspondence Create(Guid tenantId, CorrespondenceType type, string subject,
        string content, Guid authorId, CorrespondencePriority priority, bool isConfidential)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Subject required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content required.", nameof(content));
        // Number assigned by the store (unique per tenant). Placeholder replaced on insert.
        return new Correspondence(Guid.NewGuid(), tenantId, "", type, subject.Trim(), content,
            authorId, priority, isConfidential, CorrespondenceStatus.Draft, DateTimeOffset.UtcNow);
    }
}

public sealed record Correspondent(Guid Id, Guid TenantId, Guid CorrespondenceId, Guid? PersonId, string DisplayName, bool IsExternal);

public enum ApprovalStatus { Pending, Approved, Rejected, Cancelled, ChangesRequested }
public enum ApprovalPriority { Normal, Accelerated }

public sealed record Approval(
    Guid Id, Guid TenantId, string EntityType, Guid EntityId,
    Guid AssigneeId, ApprovalStatus Status, ApprovalPriority Priority,
    DateTimeOffset DueAt, DateTimeOffset? DecidedAt, Guid? DecidedBy, string? Comment);

public enum WorkTaskStatus { Open, InProgress, Done, Breached }
public enum WorkTaskPriority { Low, Normal, High, Urgent }

public sealed record WorkTask(
    Guid Id, Guid TenantId, string Title, Guid AssigneeId, Guid? ApprovalId,
    WorkTaskStatus Status, DateTimeOffset DueAt, DateTimeOffset CreatedAt,
    string Description = "", WorkTaskPriority Priority = WorkTaskPriority.Normal, int Progress = 0);

public sealed record TaskEvidence(Guid Id, Guid TenantId, Guid TaskId, Guid UploadedBy, string ObjectKey, string FileName, DateTimeOffset At);
public sealed record TaskComment(Guid Id, Guid TenantId, Guid TaskId, Guid AuthorId, string Text, DateTimeOffset At);

public enum NotificationChannel { InApp, Email, Sms }
public enum NotificationStatus { Queued, Sent, Failed }

public sealed record Notification(
    Guid Id, Guid TenantId, Guid PersonId, string Title, string Body,
    NotificationChannel Channel, NotificationStatus Status, DateTimeOffset CreatedAt);

/// <summary>Per-tenant monotonic counters (correspondence numbers, request numbers…). Row-locked per transaction.</summary>
public sealed record TenantSequence(Guid TenantId, string Scope, long NextValue);

// ============================ R3 — Governance ============================

public sealed record Committee(Guid Id, Guid TenantId, string Code, string Name, bool IsActive);

public sealed record CommitteeMember(Guid Id, Guid TenantId, Guid CommitteeId, Guid PersonId, string Role, DateTimeOffset JoinedAt);

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

public enum PolicyStatus { Draft, Published, Retired }

public sealed record Policy(Guid Id, Guid TenantId, string Code, string Title, string Content, PolicyStatus Status, int Version);

public sealed record PolicyAcknowledgement(Guid Id, Guid TenantId, Guid PolicyId, Guid PersonId, DateTimeOffset At);

// ============================ R4 — Institutional Intelligence ============================

public enum DocumentStatus { Draft, Published, Archived }

public sealed record Document(Guid Id, Guid TenantId, string Title, DocumentStatus Status, int CurrentVersion);

public sealed record DocumentVersion(
    Guid Id, Guid TenantId, Guid DocumentId, int Version,
    string ObjectKey, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

public sealed record Standard(Guid Id, Guid TenantId, string Code, string Title);

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
    Guid Id, Guid TenantId, Guid ObjectiveId, string Name, double Target, double Current, string Unit);

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
public enum CommunicationStatus { Draft, Published }

public sealed record Communication(
    Guid Id, Guid TenantId, CommunicationKind Kind, string Title, string Body,
    Guid AuthorId, bool RequiresAction, DateTimeOffset? DueAt,
    CommunicationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record CommunicationRecipient(Guid Id, Guid TenantId, Guid CommunicationId, Guid PersonId);
