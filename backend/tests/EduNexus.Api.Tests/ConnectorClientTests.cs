using System.Net;
using System.Text;
using EduNexus.Api.Connectors;
using EduNexus.Foundation;
using Xunit;

namespace EduNexus.Api.Tests;

/// <summary>
/// The four system clients against scripted answers shaped like each system's documented API. They prove the reading, the
/// guards (shape checks, same-server paging, no credential in addresses or messages) and the failure messages. They do not
/// prove the adapters work against a real Moodle, Canvas, Sakai or Banner: that needs the pilot's own system.
/// </summary>
public sealed class ConnectorClientTests
{
    private sealed class Script(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ConnectorConfig Config(ConnectorSystem s, string url = "https://lms.example.edu") =>
        new(Guid.NewGuid(), Guid.NewGuid(), s, "Test", url, "sealed", "{}", true, DateTimeOffset.UtcNow);

    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    private static Task<IReadOnlyList<CatalogCourse>> Fetch(ConnectorSystem s, Script script, string credential, IReadOnlyDictionary<string, string>? options = null, string url = "https://lms.example.edu") =>
        new CatalogClients().For(s).FetchAsync(Config(s, url), credential, options ?? NoOptions, new HttpClient(script), default);

    // ---- Moodle
    private const string MoodleOk = """
        {"courses":[
          {"id":2,"shortname":"ENG101","fullname":"English Composition","startdate":1788220800,"enddate":0,"visible":1,"enrolledusercount":42,
           "contacts":[{"id":9,"fullname":"Dr. Layla Haddad"},{"id":10,"fullname":"Omar Saleh"}]},
          {"id":3,"shortname":"OLD1","fullname":"Retired course","startdate":0,"enddate":0,"visible":0}
        ],"warnings":[]}
        """;

    [Fact]
    public async Task Moodle_Reads_Courses_And_Sends_The_Token_In_The_Body_Never_The_Address()
    {
        var script = new Script((_, _) => Json(MoodleOk));
        var courses = await Fetch(ConnectorSystem.Moodle, script, "s3cr3t-token");

        Assert.Equal(2, courses.Count);
        var eng = courses[0];
        Assert.Equal(("2", "ENG101", "English Composition", 42), (eng.ExternalId, eng.Code, eng.Title, eng.EnrolledCount));
        Assert.Equal(["Dr. Layla Haddad", "Omar Saleh"], eng.Instructors);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788220800), eng.StartsOn);
        Assert.Null(eng.EndsOn);                    // 0 means open-ended
        Assert.True(eng.IsActive);
        Assert.False(courses[1].IsActive);          // visible = 0
        Assert.Null(courses[1].EnrolledCount);

        var (req, body) = script.Seen.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://lms.example.edu/webservice/rest/server.php", req.RequestUri!.ToString());
        Assert.Contains("wstoken=s3cr3t-token", body);
        Assert.Contains("wsfunction=core_course_get_courses_by_field", body);
    }

    [Fact]
    public async Task Moodle_Errors_Come_Back_As_Messages_Without_The_Token()
    {
        var refused = new Script((_, _) => Json("""{"exception":"moodle_exception","errorcode":"invalidtoken","message":"Invalid token - token not found"}"""));
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Moodle, refused, "s3cr3t-token"));
        Assert.Contains("invalidtoken", ex.Message);
        Assert.DoesNotContain("s3cr3t-token", ex.Message);

        var noList = new Script((_, _) => Json("""{"something":"else"}"""));
        Assert.Contains("no course list", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Moodle, noList, "t"))).Message);
        var noId = new Script((_, _) => Json("""{"courses":[{"shortname":"X"}]}"""));
        Assert.Contains("no id", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Moodle, noId, "t"))).Message);
    }

    // ---- Canvas
    private static string CanvasPage(int from, int count) => System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(from, count).Select(i => new Dictionary<string, object?>
    {
        ["id"] = i, ["name"] = $"Course {i}", ["course_code"] = $"C{i}", ["start_at"] = "2026-09-01T00:00:00Z", ["end_at"] = null,
        ["workflow_state"] = "available", ["total_students"] = i,
        ["teachers"] = new[] { new { display_name = $"Teacher {i}" } }, ["term"] = new { name = "Fall 2026" },
    }));

    [Fact]
    public async Task Canvas_Follows_Next_Links_On_The_Same_Server_And_Reads_Teachers_And_Term()
    {
        var script = new Script((req, _) =>
        {
            var first = !req.RequestUri!.Query.Contains("page=2");
            var res = Json(first ? CanvasPage(1, 2) : CanvasPage(3, 1));
            if (first) res.Headers.TryAddWithoutValidation("Link", "<https://lms.example.edu/api/v1/accounts/7/courses?page=2&per_page=100>; rel=\"next\", <https://lms.example.edu/x>; rel=\"first\"");
            return res;
        });
        var courses = await Fetch(ConnectorSystem.Canvas, script, "canvas-token", new Dictionary<string, string> { ["accountId"] = "7" });

        Assert.Equal(["1", "2", "3"], courses.Select(c => c.ExternalId));
        Assert.Equal(("C1", "Course 1", "Fall 2026", 1), (courses[0].Code, courses[0].Title, courses[0].Term, courses[0].EnrolledCount));
        Assert.Equal(["Teacher 1"], courses[0].Instructors);
        Assert.Null(courses[0].EndsOn);
        Assert.Equal(2, script.Seen.Count);
        Assert.All(script.Seen, s => Assert.Equal("Bearer canvas-token", s.Request.Headers.Authorization!.ToString()));
        Assert.StartsWith("https://lms.example.edu/api/v1/accounts/7/courses?", script.Seen[0].Request.RequestUri!.ToString());
        Assert.DoesNotContain("canvas-token", script.Seen[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Canvas_Refuses_A_Next_Link_On_Another_Server_And_A_Wrong_Shape()
    {
        var hostile = new Script((_, _) =>
        {
            var res = Json(CanvasPage(1, 1));
            res.Headers.TryAddWithoutValidation("Link", "<https://evil.example.net/steal?page=2>; rel=\"next\"");
            return res;
        });
        Assert.Contains("different server", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Canvas, hostile, "t"))).Message);
        Assert.Single(hostile.Seen);   // the second request was never made

        var notList = new Script((_, _) => Json("""{"errors":[{"message":"not found"}]}"""));
        Assert.Contains("not a list", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Canvas, notList, "t"))).Message);
    }

    // ---- Sakai
    [Fact]
    public async Task Sakai_Signs_In_Then_Lists_Only_Sites_Of_The_Wanted_Type()
    {
        var script = new Script((req, _) => req.RequestUri!.AbsolutePath == "/direct/session"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("abc123-session") }
            : Json("""{"site_collection":[{"id":"s1","title":"Biology 101","type":"course","props":{"term":"Fall 2026"}},{"id":"s2","title":"Staff room","type":"project"},{"id":"s3","title":"Draft","type":"course","published":"false"}]}"""));
        var courses = await Fetch(ConnectorSystem.Sakai, script, "svc-account:p@ss:word");

        Assert.Equal(["s1", "s3"], courses.Select(c => c.ExternalId));
        Assert.Equal("Fall 2026", courses[0].Term);
        Assert.True(courses[0].IsActive);
        Assert.False(courses[1].IsActive);
        Assert.Contains("_username=svc-account", script.Seen[0].Body);
        Assert.Contains("_password=p%40ss%3Aword", script.Seen[0].Body);   // only the first colon splits the credential
        Assert.Equal("JSESSIONID=abc123-session", script.Seen[1].Request.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task Sakai_Rejects_A_Malformed_Credential_And_A_Session_That_Is_Not_An_Id()
    {
        Assert.Contains("username:password", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Sakai, new Script((_, _) => Json("{}")), "just-a-token"))).Message);
        var html = new Script((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Please sign in</html>") });
        Assert.Contains("not a session id", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Sakai, html, "u:p"))).Message);
    }

    // ---- Banner (Ethos)
    private const string Jwt = "aaa.bbb.ccc";
    private static string Sections(int from, int count, string status = "open") => System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(from, count).Select(i => new Dictionary<string, object?>
    {
        ["id"] = $"guid-{i}", ["code"] = $"MATH-{i}", ["titles"] = new[] { new { value = $"Calculus {i}" } },
        ["startOn"] = "2026-09-01T00:00:00Z", ["endOn"] = "2026-12-20T00:00:00Z",
        ["academicPeriod"] = new { id = "fall26" }, ["status"] = new { category = status },
    }));

    [Fact]
    public async Task Banner_Exchanges_The_Key_Then_Reads_Sections_Page_By_Page_With_The_Token_Not_The_Key()
    {
        var script = new Script((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/auth") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Jwt) };
            var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["offset"]!);
            return Json(offset == 0 ? Sections(1, 100) : Sections(101, 5, "cancelled"));
        });
        var courses = await Fetch(ConnectorSystem.Banner, script, "ethos-api-key", url: "https://banner.example.edu");

        Assert.Equal(105, courses.Count);
        Assert.Equal(("guid-1", "MATH-1", "Calculus 1", "fall26"), (courses[0].ExternalId, courses[0].Code, courses[0].Title, courses[0].Term));
        Assert.True(courses[0].IsActive);
        Assert.All(courses.Skip(100), c => Assert.False(c.IsActive));      // cancelled
        Assert.Equal("Bearer ethos-api-key", script.Seen[0].Request.Headers.Authorization!.ToString());
        Assert.All(script.Seen.Skip(1), s => Assert.Equal($"Bearer {Jwt}", s.Request.Headers.Authorization!.ToString()));
        Assert.Equal(3, script.Seen.Count);                                 // auth + two pages (the short page ends it)
        Assert.Contains("vnd.hedtech.integration.v16", script.Seen[1].Request.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public async Task Banner_Rejects_An_Answer_That_Is_Not_A_Token_Or_Not_A_List()
    {
        var notToken = new Script((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Welcome") });
        Assert.Contains("not a token", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Banner, notToken, "k"))).Message);
        var notList = new Script((req, _) => req.RequestUri!.AbsolutePath == "/auth" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Jwt) } : Json("""{"errors":[]}"""));
        Assert.Contains("not a list of sections", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Banner, notList, "k"))).Message);
    }

    // ---- Shared guards
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the credential")]
    [InlineData(HttpStatusCode.Forbidden, "refused the credential")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    [InlineData(HttpStatusCode.Redirect, "redirected")]
    public async Task Failures_Are_Reported_Plainly_And_Never_Include_The_Credential(HttpStatusCode code, string expected)
    {
        var script = new Script((_, _) => new HttpResponseMessage(code) { Content = new StringContent("secret-credential appears in this body") });
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Canvas, script, "secret-credential"));
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret-credential", ex.Message);
    }

    [Fact]
    public async Task An_Answer_That_Is_Not_Json_Or_Is_Unreachable_Is_Explained()
    {
        var html = new Script((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        Assert.Contains("not JSON", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Moodle, html, "t"))).Message);
        var down = new Script((_, _) => throw new HttpRequestException("Name or service not known"));
        Assert.Contains("could not be reached", (await Assert.ThrowsAsync<ConnectorException>(() => Fetch(ConnectorSystem.Moodle, down, "t"))).Message);
    }

    [Fact]
    public void A_File_Connector_Has_No_Live_Client()
    {
        Assert.Throws<ConnectorException>(() => new CatalogClients().For(ConnectorSystem.Csv));
    }
}
