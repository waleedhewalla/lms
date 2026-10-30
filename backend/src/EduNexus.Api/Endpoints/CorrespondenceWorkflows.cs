using System.Text.Json;
using EduNexus.Foundation;

namespace EduNexus.Api.Endpoints;

/// <summary>
/// The standard workflow of each correspondence type, stored as ordinary workflow definitions so the university can edit them
/// (<c>POST /api/workflows/definitions/{id}/new-version</c>). Every approval step has a deadline in working days, tells its approver,
/// and is skipped when its approver is the sender or already approved the letter. The money limits are provisional until the
/// university sets them.
/// </summary>
public static class CorrespondenceWorkflows
{
    /// <summary>Provisional financial limits (in the tenant's currency): above the first the VP signs too, above the second the President.</summary>
    public const decimal VpLimit = 50_000m;
    public const decimal PresidentLimit = 250_000m;

    public static IReadOnlyList<(string Code, string Name, string NodesJson)> Defaults { get; } =
    [
        Flow("CORR_ADMIN_MEMO", "Administrative memo",
            By("line_manager", "Line manager review", "submitter_head", 3),
            By("recipient", "Recipient action", "recipient", 3)),
        Flow("CORR_ACADEMIC", "Academic correspondence",
            By("line_manager", "Head of department review", "submitter_head", 3),
            By("dean", "Dean approval", "submitter_dean", 3),
            By("receiving_head", "Receiving college review", "target_head", 3)),
        Flow("CORR_FINANCIAL", "Financial correspondence",
            By("line_manager", "Line manager review", "submitter_head", 3),
            Role("finance", "Finance manager review", "FINANCE_MANAGER", 3),
            Role("vp", "Vice president approval", "VP_ADMIN_FINANCE", 3, Over(VpLimit)),
            Role("president", "President approval", "PRESIDENT", 5, Over(PresidentLimit)),
            By("finance_exec", "Finance execution", "target_head", 3)),
        Flow("CORR_INTER_UNIT", "Between departments",
            By("line_manager", "Head of sending department", "submitter_head", 3),
            By("receiving_head", "Head of receiving department", "target_head", 3)),
        Flow("CORR_EMPLOYEE_REQUEST", "Employee request",
            By("line_manager", "Line manager approval", "submitter_head", 3),
            Role("hr", "Human resources decision", "HR_MANAGER", 5)),
        Flow("CORR_STUDENT_REQUEST", "Student request",
            Role("student_affairs", "Student affairs decision", "STUDENT_AFFAIRS", 5)),
        Flow("CORR_DECISION", "Decision",
            Role("legal", "Legal review", "LEGAL_ADVISOR", 3),
            By("recipient", "Notification of those concerned", "recipient", 2)),
        Flow("CORR_CIRCULAR", "Circular",
            By("line_manager", "Line manager review", "submitter_head", 2),
            By("dean", "Dean approval", "submitter_dean", 3)),
        Flow("CORR_COLLEGE_ADMIN", "Between college administrations",
            By("sender_head", "Head of the sending administration", "submitter_head", 3),
            By("receiving_head", "Head of the receiving administration", "target_head", 3)),
        Flow("CORR_COLLEGE_DEPT", "Between departments of a college",
            By("sender_head", "Head of the sending department", "submitter_head", 3),
            By("receiving_head", "Head of the receiving department", "target_head", 3)),
        Flow("CORR_TO_DEAN", "To the dean or vice dean",
            By("line_manager", "Line manager review", "submitter_head", 2),
            By("vice_dean", "Vice dean review", "target_deputy", 3),
            By("dean", "Dean decision", "target_head", 3)),
        Flow("CORR_INFO_REQUEST", "Information request or complaint",
            By("competent_unit", "Competent unit answer", "recipient", 5)),
    ];

    private static Dictionary<string, object?> Step(string id, string label, int days, Dictionary<string, object?>? when) =>
        new() { ["id"] = id, ["type"] = "approval", ["label"] = label, ["slaDays"] = days, ["workingDays"] = true, ["notify"] = true, ["skipDuplicate"] = true, ["when"] = when };

    private static Dictionary<string, object?> By(string id, string label, string from, int days, Dictionary<string, object?>? when = null)
    { var s = Step(id, label, days, when); s["assigneeFrom"] = from; return s; }

    private static Dictionary<string, object?> Role(string id, string label, string role, int days, Dictionary<string, object?>? when = null)
    { var s = Step(id, label, days, when); s["roleCode"] = role; return s; }

    private static Dictionary<string, object?> Over(decimal amount) => new() { ["field"] = "amount", ["op"] = ">", ["value"] = amount };

    private static (string, string, string) Flow(string code, string name, params Dictionary<string, object?>[] steps)
    {
        var nodes = new List<Dictionary<string, object?>> { new() { ["id"] = "start", ["type"] = "start" } };
        nodes.AddRange(steps.Select(s => s.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)));
        nodes.Add(new() { ["id"] = "close", ["type"] = "close", ["label"] = "Closed" });
        return (code, name, JsonSerializer.Serialize(nodes));
    }
}
