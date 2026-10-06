namespace EduNexus.Api.Auth;

/// <summary>
/// Every permission code the API checks. <c>GET /api/permissions</c> serves it to role editors;
/// a test fails if an endpoint checks a code that is missing here.
/// </summary>
public static class PermissionCatalog
{
    public static readonly string[] All =
    [
        "action:update", "action:verify",
        "activity:read", "activity:write",
        "ai:admin", "ai:ask", "ai:manage", "ai:read", "ai:write",
        "analytics:read",
        "approval:decide", "approval:read",
        "audit:read",
        "calendar:manage", "calendar:read",
        "chatter:read", "chatter:write",
        "committee:create", "committee:read",
        "communication:create", "communication:read",
        "correspondence:confidential", "correspondence:create", "correspondence:read",
        "decision:create", "decision:read",
        "document:create", "document:manage", "document:read", "document:share", "document:update", "document:write",
        "form:manage", "form:read",
        "inbox:read",
        "integration:admin", "integration:manage", "integration:read", "integration:write",
        "meeting:create", "meeting:read", "meeting:update",
        "minutes:approve",
        "notification:manage", "notification:read",
        "org:create", "org:read",
        "person:create", "person:read", "person:update",
        "policy:create", "policy:read",
        "quality:manage", "quality:read",
        "request:create", "request:read",
        "role:assign", "role:create", "role:read",
        "search:read",
        "sla:manage",
        "strategy:manage", "strategy:read",
        "task:create", "task:read", "task:update", "task:verify",
        "tenant:create", "tenant:read",
        "workflow:manage", "workflow:read",
    ];
}
