using System.Text.Json;

namespace EduNexus.Foundation;

/// <summary>
/// Server-side validation of FormSubmission.DataJson against Form.SchemaJson.
/// Schema: [{key,label,type,required,options[],visibleWhen:{field,equals}}].
/// </summary>
public static class FormValidation
{
    public static List<string> Validate(string schemaJson, string dataJson)
    {
        var errors = new List<string>();
        JsonDocument schema, data;
        try { schema = JsonDocument.Parse(schemaJson); }
        catch { return ["invalid form schema"]; }
        try { data = JsonDocument.Parse(dataJson); }
        catch { return ["submission is not valid JSON"]; }
        if (schema.RootElement.ValueKind != JsonValueKind.Array) return ["form schema must be an array"];
        if (data.RootElement.ValueKind != JsonValueKind.Object) return ["submission must be an object"];
        using (schema)
        using (data)
        {
            foreach (var field in schema.RootElement.EnumerateArray())
            {
                if (!field.TryGetProperty("key", out var keyEl)) { errors.Add("schema field without key"); continue; }
                var key = keyEl.GetString() ?? "";
                var type = field.TryGetProperty("type", out var t) ? t.GetString() ?? "Text" : "Text";
                var required = field.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                if (IsHidden(field, data.RootElement)) continue;
                var has = data.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && (value.ValueKind != JsonValueKind.String || value.GetString() is not "");
                if (!has)
                {
                    if (required) errors.Add($"{key}: required");
                    continue;
                }
                errors.AddRange(ValidateType(key, type, value, field));
            }
        }
        return errors;
    }

    private static bool IsHidden(JsonElement field, JsonElement data)
    {
        if (!field.TryGetProperty("visibleWhen", out var cond) || cond.ValueKind != JsonValueKind.Object)
            return false;
        if (!cond.TryGetProperty("field", out var f) || !cond.TryGetProperty("equals", out var expected))
            return false;
        if (!data.TryGetProperty(f.GetString() ?? "", out var actual)) return true;
        return actual.ToString() != expected.ToString();
    }

    private static IEnumerable<string> ValidateType(string key, string type, JsonElement value, JsonElement field)
    {
        string[] Options() => field.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.ToString()).ToArray() : [];
        switch (type)
        {
            case "Number" or "Currency":
                if (value.ValueKind != JsonValueKind.Number) yield return $"{key}: must be a number";
                break;
            case "Checkbox":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    yield return $"{key}: must be true/false";
                break;
            case "Date" or "DateTime":
                if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), out _))
                    yield return $"{key}: must be a date";
                break;
            case "Dropdown" or "Radio":
                if (!Options().Contains(value.ToString())) yield return $"{key}: value not in options";
                break;
            case "MultiSelect":
                if (value.ValueKind != JsonValueKind.Array
                    || value.EnumerateArray().Any(x => !Options().Contains(x.ToString())))
                    yield return $"{key}: values must be a subset of options";
                break;
            case "Table":
                if (value.ValueKind != JsonValueKind.Array) yield return $"{key}: must be an array of rows";
                break;
        }
    }
}
