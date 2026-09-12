using System.Text.Json;

namespace EduNexus.Foundation;

/// <summary>Validates workflow definition node graphs at creation time (fail fast, not at runtime).</summary>
public static class WorkflowDefinitionValidator
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "approval", "task", "notification", "end",
    };

    public static void Validate(string nodesJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(nodesJson); }
        catch { throw new ArgumentException("NodesJson must be valid JSON."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                throw new ArgumentException("NodesJson must be a non-empty array.");
            var nodes = doc.RootElement.EnumerateArray().ToList();
            var ids = new HashSet<string>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (!n.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
                    throw new ArgumentException($"Node {i} needs an id.");
                if (!ids.Add(idEl.GetString()!)) throw new ArgumentException($"Duplicate node id '{idEl}'.");
                if (!n.TryGetProperty("type", out var tEl) || !KnownTypes.Contains(tEl.GetString() ?? ""))
                    throw new ArgumentException($"Node '{idEl}' has unknown type (v1: start/approval/task/notification/end).");
            }
            if (nodes[0].GetProperty("type").GetString() is not "start")
                throw new ArgumentException("First node must be 'start'.");
        }
    }
}
