using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EduNexus.Foundation;

namespace EduNexus.Api.Connectors;

/// <summary>A course in the neutral shape every system is read into. No student is named anywhere in it.</summary>
public sealed record CatalogCourse(string ExternalId, string Code, string Title, string? Term, DateTimeOffset? StartsOn, DateTimeOffset? EndsOn,
    int? EnrolledCount, IReadOnlyList<string> Instructors, bool IsActive);

/// <summary>
/// The system answered, but not in a way we can use (wrong address, refused credential, an answer in an unexpected shape).
/// The message is shown to the administrator, so it never contains the credential or the raw response.
/// </summary>
public sealed class ConnectorException(string message) : Exception(message);

public interface ICatalogClient
{
    Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig connector, string credential, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct);
}

public interface ICatalogClients
{
    ICatalogClient For(ConnectorSystem system);
}

public sealed class CatalogClients : ICatalogClients
{
    public ICatalogClient For(ConnectorSystem system) => system switch
    {
        ConnectorSystem.Moodle => new MoodleClient(),
        ConnectorSystem.Canvas => new CanvasClient(),
        ConnectorSystem.Sakai => new SakaiClient(),
        ConnectorSystem.Banner => new BannerClient(),
        _ => throw new ConnectorException($"{system} is loaded from a file, not read from a server."),
    };
}

/// <summary>Shared limits and helpers: an answer is size-capped, a catalogue is size-capped, and timestamps are read defensively.</summary>
internal static class Wire
{
    public const int MaxCourses = 20_000;
    public const int MaxPages = 200;
    private const int MaxBytes = 20 * 1024 * 1024;

    public static async Task<(HttpResponseMessage Response, string Body)> SendAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) { throw new ConnectorException($"{what}: the server could not be reached ({ex.Message.Split('\n')[0]})."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ConnectorException($"{what}: the server did not answer in time."); }
        if (res.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectKeepVerb or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            throw new ConnectorException($"{what}: the server redirected the request, which is not followed. Use the address it redirects to.");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ConnectorException($"{what}: the server refused the credential ({(int)res.StatusCode}).");
        if (!res.IsSuccessStatusCode) throw new ConnectorException($"{what}: the server answered {(int)res.StatusCode}.");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxBytes) throw new ConnectorException($"{what}: the answer is larger than {MaxBytes / (1024 * 1024)} MB.");
        }
        return (res, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public static JsonElement Parse(string body, string what)
    {
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new ConnectorException($"{what}: the answer is not JSON (is the address the server's API address?)."); }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    public static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
              : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null
            : null;

    /// <summary>Unix seconds; 0 and absent mean "no date" (Moodle uses 0 for an open-ended course).</summary>
    public static DateTimeOffset? UnixSeconds(JsonElement e, string name) =>
        Int64(e, name) is > 0 and var s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    public static DateTimeOffset? UnixMillis(JsonElement e, string name) =>
        Int64(e, name) is > 0 and var s ? DateTimeOffset.FromUnixTimeMilliseconds(s) : null;

    private static long? Int64(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public static DateTimeOffset? Iso(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string Base(ConnectorConfig c) => (c.BaseUrl ?? throw new ConnectorException("The connector has no server address.")).TrimEnd('/');
}

/// <summary>Moodle web services (REST): core_course_get_courses_by_field. The token goes in the POST body, never in the address or the logs.</summary>
internal sealed class MoodleClient : ICatalogClient
{
    public async Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig c, string token, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Wire.Base(c)}/webservice/rest/server.php")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["wstoken"] = token, ["wsfunction"] = "core_course_get_courses_by_field", ["moodlewsrestformat"] = "json" }),
        };
        var (_, body) = await Wire.SendAsync(http, req, "Moodle", ct);
        var root = Wire.Parse(body, "Moodle");
        // Moodle reports its own errors with HTTP 200 and an "exception" object.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("exception", out _))
            throw new ConnectorException($"Moodle: {Wire.Str(root, "errorcode") ?? "error"}: {Wire.Str(root, "message") ?? "the request was refused"}.");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("courses", out var courses) || courses.ValueKind != JsonValueKind.Array)
            throw new ConnectorException("Moodle: the answer has no course list (is the web service for course lookup enabled for this token?).");
        var list = new List<CatalogCourse>();
        foreach (var x in courses.EnumerateArray())
        {
            var id = Wire.Str(x, "id") ?? throw new ConnectorException("Moodle: a course has no id.");
            var code = Wire.Str(x, "shortname") ?? id;
            var contacts = x.TryGetProperty("contacts", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.EnumerateArray().Select(k => Wire.Str(k, "fullname")).OfType<string>().ToList() : [];
            list.Add(new CatalogCourse(id, code, Wire.Str(x, "fullname") ?? code, null, Wire.UnixSeconds(x, "startdate"), Wire.UnixSeconds(x, "enddate"),
                Wire.Int(x, "enrolledusercount"), contacts, Wire.Int(x, "visible") != 0));
            if (list.Count > Wire.MaxCourses) throw new ConnectorException($"Moodle: more than {Wire.MaxCourses} courses; narrow the token's scope.");
        }
        return list;
    }
}

/// <summary>Canvas REST: /api/v1/accounts/{accountId}/courses, following Link: rel="next" within the same server only.</summary>
internal sealed class CanvasClient : ICatalogClient
{
    public async Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig c, string token, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct)
    {
        var account = Uri.EscapeDataString(options.GetValueOrDefault("accountId", "self"));
        var baseUri = new Uri(Wire.Base(c) + "/");
        var next = new Uri(baseUri, $"api/v1/accounts/{account}/courses?include[]=teachers&include[]=total_students&include[]=term&per_page=100");
        var list = new List<CatalogCourse>();
        for (var page = 0; next is not null; page++)
        {
            if (page >= Wire.MaxPages) throw new ConnectorException($"Canvas: more than {Wire.MaxPages} pages of courses.");
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (res, body) = await Wire.SendAsync(http, req, "Canvas", ct);
            var root = Wire.Parse(body, "Canvas");
            if (root.ValueKind != JsonValueKind.Array) throw new ConnectorException("Canvas: the answer is not a list of courses (is the account id right?).");
            foreach (var x in root.EnumerateArray())
            {
                var id = Wire.Str(x, "id") ?? throw new ConnectorException("Canvas: a course has no id.");
                var code = Wire.Str(x, "course_code") ?? id;
                var teachers = x.TryGetProperty("teachers", out var ts) && ts.ValueKind == JsonValueKind.Array
                    ? ts.EnumerateArray().Select(t => Wire.Str(t, "display_name")).OfType<string>().ToList() : [];
                var term = x.TryGetProperty("term", out var tm) ? Wire.Str(tm, "name") : null;
                list.Add(new CatalogCourse(id, code, Wire.Str(x, "name") ?? code, term, Wire.Iso(x, "start_at"), Wire.Iso(x, "end_at"),
                    Wire.Int(x, "total_students"), teachers, Wire.Str(x, "workflow_state") is "available" or "completed" or null));
                if (list.Count > Wire.MaxCourses) throw new ConnectorException($"Canvas: more than {Wire.MaxCourses} courses.");
            }
            next = NextLink(res, baseUri);
        }
        return list;
    }

    /// <summary>The next page's address, only when it is on the same server (a hostile answer cannot steer the request elsewhere).</summary>
    internal static Uri? NextLink(HttpResponseMessage res, Uri baseUri)
    {
        if (!res.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var seg = part.Split(';');
            if (seg.Skip(1).Any(s => s.Trim().Replace(" ", "").Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            {
                var url = seg[0].Trim().Trim('<', '>');
                if (!Uri.TryCreate(baseUri, url, out var u)) throw new ConnectorException("Canvas: the next-page address is not valid.");
                if (!string.Equals(u.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(u.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
                    throw new ConnectorException("Canvas: the next page is on a different server, so it was not followed.");
                return u;
            }
        }
        return null;
    }
}

/// <summary>
/// Sakai Entity Broker (/direct): sign in with a service account, then list sites of one type (default "course").
/// Sakai does not report enrolment counts or instructors on this call, so those stay empty. Needs validating against the
/// site's own Sakai version: older and newer releases differ.
/// </summary>
internal sealed class SakaiClient : ICatalogClient
{
    public async Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig c, string credential, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct)
    {
        var colon = credential.IndexOf(':');
        if (colon <= 0) throw new ConnectorException("Sakai: the credential must be 'username:password'.");
        var cookieName = options.GetValueOrDefault("sessionCookie", "JSESSIONID");
        using var login = new HttpRequestMessage(HttpMethod.Post, $"{Wire.Base(c)}/direct/session")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_username"] = credential[..colon], ["_password"] = credential[(colon + 1)..] }),
        };
        var (_, session) = await Wire.SendAsync(http, login, "Sakai sign-in", ct);
        var sessionId = session.Trim();
        if (sessionId.Length == 0 || sessionId.Length > 200 || sessionId.Any(ch => char.IsWhiteSpace(ch) || ch is ';' or ','))
            throw new ConnectorException("Sakai sign-in: the answer is not a session id.");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Wire.Base(c)}/direct/site.json?_limit={Wire.MaxCourses + 1}");
        req.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={sessionId}");
        var (_, body) = await Wire.SendAsync(http, req, "Sakai", ct);
        var root = Wire.Parse(body, "Sakai");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("site_collection", out var sites) || sites.ValueKind != JsonValueKind.Array)
            throw new ConnectorException("Sakai: the answer has no site list (is the account allowed to list sites?).");
        var wanted = options.GetValueOrDefault("siteType", "course");
        var list = new List<CatalogCourse>();
        foreach (var x in sites.EnumerateArray())
        {
            if (!string.Equals(Wire.Str(x, "type"), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var id = Wire.Str(x, "id") ?? throw new ConnectorException("Sakai: a site has no id.");
            var title = Wire.Str(x, "title") ?? id;
            var term = x.TryGetProperty("props", out var p) ? Wire.Str(p, "term") : null;
            list.Add(new CatalogCourse(id, id, title, term, null, null, null, [], Wire.Str(x, "published") is not "false"));
            if (list.Count > Wire.MaxCourses) throw new ConnectorException($"Sakai: more than {Wire.MaxCourses} sites.");
        }
        return list;
    }
}

/// <summary>
/// Ellucian Banner through the Ethos Integration API (EEDM "sections"): the credential is an Ethos API key, exchanged for a
/// short-lived token at the auth address, then sections are read page by page. Instructors and counts need further calls
/// that differ between sites, so they are left empty. Needs validating against the site's own Ethos version.
/// </summary>
internal sealed class BannerClient : ICatalogClient
{
    public async Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig c, string apiKey, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct)
    {
        var baseUrl = Wire.Base(c);
        var authUrl = options.GetValueOrDefault("ethosAuthUrl", $"{baseUrl}/auth");
        using var auth = new HttpRequestMessage(HttpMethod.Post, authUrl);
        auth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var (_, jwt) = await Wire.SendAsync(http, auth, "Banner sign-in", ct);
        jwt = jwt.Trim().Trim('"');
        if (jwt.Length == 0 || jwt.Split('.').Length != 3) throw new ConnectorException("Banner sign-in: the answer is not a token.");

        var accept = options.GetValueOrDefault("acceptVersion", "application/vnd.hedtech.integration.v16+json");
        var list = new List<CatalogCourse>();
        const int pageSize = 100;
        for (var page = 0; page < Wire.MaxPages; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sections?limit={pageSize}&offset={page * pageSize}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            req.Headers.TryAddWithoutValidation("Accept", accept);
            var (_, body) = await Wire.SendAsync(http, req, "Banner", ct);
            var root = Wire.Parse(body, "Banner");
            if (root.ValueKind != JsonValueKind.Array) throw new ConnectorException("Banner: the answer is not a list of sections (check the accept version).");
            foreach (var x in root.EnumerateArray())
            {
                var id = Wire.Str(x, "id") ?? throw new ConnectorException("Banner: a section has no id.");
                var code = Wire.Str(x, "code") ?? id;
                var title = x.TryGetProperty("titles", out var ts) && ts.ValueKind == JsonValueKind.Array
                    ? ts.EnumerateArray().Select(t => Wire.Str(t, "value")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) : null;
                var term = x.TryGetProperty("academicPeriod", out var ap) ? Wire.Str(ap, "id") : null;
                var status = x.TryGetProperty("status", out var st) ? (Wire.Str(st, "category") ?? Wire.Str(st, "id")) : Wire.Str(x, "status");
                list.Add(new CatalogCourse(id, code, title ?? code, term, Wire.Iso(x, "startOn"), Wire.Iso(x, "endOn"), null, [],
                    !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)));
                if (list.Count > Wire.MaxCourses) throw new ConnectorException($"Banner: more than {Wire.MaxCourses} sections.");
            }
            if (root.GetArrayLength() < pageSize) return list;
        }
        throw new ConnectorException($"Banner: more than {Wire.MaxPages} pages of sections.");
    }
}
