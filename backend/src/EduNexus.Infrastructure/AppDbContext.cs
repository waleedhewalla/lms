using EduNexus.Foundation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace EduNexus.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Campus> Campuses => Set<Campus>();
    public DbSet<OrganizationalUnit> OrganizationalUnits => Set<OrganizationalUnit>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuthorityDelegation> AuthorityDelegations => Set<AuthorityDelegation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<Correspondence> Correspondences => Set<Correspondence>();
    public DbSet<Correspondent> Correspondents => Set<Correspondent>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<TenantSequence> TenantSequences => Set<TenantSequence>();
    public DbSet<Committee> Committees => Set<Committee>();
    public DbSet<CommitteeMember> CommitteeMembers => Set<CommitteeMember>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<AgendaItem> AgendaItems => Set<AgendaItem>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionAction> DecisionActions => Set<DecisionAction>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyAcknowledgement> PolicyAcknowledgements => Set<PolicyAcknowledgement>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<DocumentShare> DocumentShares => Set<DocumentShare>();
    public DbSet<Standard> Standards => Set<Standard>();
    public DbSet<Criterion> Criteria => Set<Criterion>();
    public DbSet<Evidence> Evidences => Set<Evidence>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<CorrectiveAction> CorrectiveActions => Set<CorrectiveAction>();
    public DbSet<StrategicPlan> StrategicPlans => Set<StrategicPlan>();
    public DbSet<Objective> Objectives => Set<Objective>();
    public DbSet<Kpi> Kpis => Set<Kpi>();
    public DbSet<AiInteraction> AiInteractions => Set<AiInteraction>();
    public DbSet<IntegrationEndpoint> IntegrationEndpoints => Set<IntegrationEndpoint>();
    public DbSet<IntegrationDelivery> IntegrationDeliveries => Set<IntegrationDelivery>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Form> Forms => Set<Form>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<Communication> Communications => Set<Communication>();
    public DbSet<CommunicationRecipient> CommunicationRecipients => Set<CommunicationRecipient>();
    public DbSet<TaskEvidence> TaskEvidences => Set<TaskEvidence>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<NotificationReceipt> NotificationReceipts => Set<NotificationReceipt>();
    public DbSet<ScheduledActivity> ScheduledActivities => Set<ScheduledActivity>();
    public DbSet<RecordComment> RecordComments => Set<RecordComment>();
    public DbSet<EntityFollower> EntityFollowers => Set<EntityFollower>();
    public DbSet<DocumentWorkspace> DocumentWorkspaces => Set<DocumentWorkspace>();
    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();
    public DbSet<MeetingVote> MeetingVotes => Set<MeetingVote>();
    public DbSet<DocumentRetentionPolicy> DocumentRetentionPolicies => Set<DocumentRetentionPolicy>();
    public DbSet<CorrespondenceRoutingSlip> CorrespondenceRoutingSlips => Set<CorrespondenceRoutingSlip>();
    public DbSet<DocumentActionRule> DocumentActionRules => Set<DocumentActionRule>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<MeetingMinutes> MeetingMinutes => Set<MeetingMinutes>();
    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();
    public DbSet<Procedure> Procedures => Set<Procedure>();
    public DbSet<DecisionActionEvidence> DecisionActionEvidences => Set<DecisionActionEvidence>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    public async Task SetTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Per-session GUC consumed by RLS policies (see migration SQL).
        await Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant', {0}, false)", new[] { tenantId.ToString() }, ct);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var permsConverter = new ValueConverter<IReadOnlyList<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

        b.Entity<Tenant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        b.Entity<Organization>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        b.Entity<Campus>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<OrganizationalUnit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<Person>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Email);
            e.Property(x => x.FullName).HasMaxLength(250).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Role>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Permissions).HasConversion(permsConverter).HasColumnType("jsonb");
        });
        b.Entity<RoleAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PersonId, x.RoleId }).IsUnique();
        });
        b.Entity<AuthorityDelegation>(e => e.HasKey(x => x.Id));
        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.At });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
        b.Entity<OutboxEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DispatchedAt);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<Correspondence>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.ParentCorrespondenceId });
            e.Property(x => x.Number).HasMaxLength(30).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Correspondent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CorrespondenceId });
        });
        b.Entity<Approval>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<WorkTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.AssigneeId });
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<TaskEvidence>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.TaskId });
        });
        b.Entity<TaskComment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.TaskId });
            e.Property(x => x.Text).HasMaxLength(2000).IsRequired();
        });
        b.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PersonId, x.Status });
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<NotificationTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code, x.Channel }).IsUnique();
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<NotificationReceipt>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.NotificationId });
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<TenantSequence>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Scope });
        });
        b.Entity<Committee>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<CommitteeMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CommitteeId, x.PersonId }).IsUnique();
        });
        b.Entity<Meeting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CommitteeId });
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<AgendaItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.MeetingId, x.Order }).IsUnique();
        });
        b.Entity<Attendance>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.MeetingId, x.PersonId }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Decision>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.MeetingId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<DecisionAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DecisionId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Policy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<PolicyAcknowledgement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PolicyId, x.PersonId }).IsUnique();
        });
        b.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Classification).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<DocumentShare>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DocumentId, x.PersonId }).IsUnique();
        });
        b.Entity<DocumentVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DocumentId, x.Version }).IsUnique();
        });
        b.Entity<Standard>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<Criterion>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.StandardId, x.Code }).IsUnique();
        });
        b.Entity<Evidence>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CriterionId });
        });
        b.Entity<Finding>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CriterionId });
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<CorrectiveAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.FindingId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<StrategicPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
        });
        b.Entity<Objective>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PlanId, x.Code }).IsUnique();
        });
        b.Entity<Kpi>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ObjectiveId });
        });
        b.Entity<AiInteraction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.At });
            e.Property(x => x.Capability).HasMaxLength(50).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
        });
        b.Entity<IntegrationEndpoint>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EventType });
        });
        b.Entity<IntegrationDelivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EndpointId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Request>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.Category });
            e.Property(x => x.Number).HasMaxLength(30).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Form>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.SchemaJson).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<FormSubmission>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.RequestId });
            e.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<WorkflowDefinition>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.NodesJson).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<WorkflowInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ContextJson).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<Communication>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Kind });
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<CommunicationRecipient>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CommunicationId, x.PersonId }).IsUnique();
        });
        b.Entity<ScheduledActivity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.TenantId, x.AssigneeId, x.IsCompleted });
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Summary).HasMaxLength(500).IsRequired();
        });
        b.Entity<RecordComment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.Property(x => x.Content).IsRequired();
        });
        b.Entity<EntityFollower>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId, x.PersonId }).IsUnique();
        });
        b.Entity<DocumentWorkspace>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        b.Entity<DocumentTag>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DocumentId });
            e.Property(x => x.TagCategory).HasMaxLength(50).IsRequired();
            e.Property(x => x.TagValue).HasMaxLength(100).IsRequired();
        });
        b.Entity<MeetingVote>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.MeetingId, x.AgendaItemId, x.PersonId }).IsUnique();
            e.Property(x => x.Choice).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Remarks).HasMaxLength(500);
        });
        b.Entity<DocumentRetentionPolicy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DocumentId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.NextReviewDueAt });
            e.Property(x => x.Standard).HasMaxLength(100).IsRequired();
            e.Property(x => x.DispositionAction).HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(1000);
        });
        b.Entity<CorrespondenceRoutingSlip>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CorrespondenceId });
            e.HasIndex(x => new { x.TenantId, x.ToPersonId, x.IsCompleted });
            e.Property(x => x.ActionRequired).HasMaxLength(100).IsRequired();
            e.Property(x => x.Instructions).IsRequired();
        });
        b.Entity<DocumentActionRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.TriggerCategory, x.TriggerValue, x.IsActive });
            e.Property(x => x.TriggerCategory).HasMaxLength(50).IsRequired();
            e.Property(x => x.TriggerValue).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActionType).HasMaxLength(50).IsRequired();
            e.Property(x => x.TargetValue).HasMaxLength(500);
        });
        // --- R0.2 Wave C ---
        b.Entity<CalendarEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.StartsAt });
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.SourceType).HasMaxLength(50);
        });
        b.Entity<MeetingMinutes>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.MeetingId, x.Version }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Content).IsRequired();
        });
        b.Entity<PolicyVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PolicyId, x.Version }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.ChangeNote).HasMaxLength(1000);
        });
        b.Entity<Procedure>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        });
        b.Entity<DecisionActionEvidence>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ActionId });
            e.Property(x => x.ObjectKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        });
        b.Entity<SlaPolicy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.EntityType }).IsUnique();
            e.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
        });
        b.Entity<NotificationPreference>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PersonId, x.Channel }).IsUnique();
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MinPriority).HasConversion<string>().HasMaxLength(20);
        });
    }
}
