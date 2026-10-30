using EduNexus.Api.Connectors;
using EduNexus.Foundation;

namespace EduNexus.Api.Tests;

/// <summary>Stands in for the other systems: returns what a test scripts, and records what it was asked with (including the opened credential).</summary>
public sealed class FakeCatalogClients : ICatalogClients, ICatalogClient
{
    public List<CatalogCourse> Courses { get; set; } = [];
    public string? FailWith { get; set; }
    public string? LastCredential { get; private set; }
    public string? LastBaseUrl { get; private set; }
    public IReadOnlyDictionary<string, string>? LastOptions { get; private set; }
    public ConnectorSystem? LastSystem { get; private set; }

    public void Reset() { Courses = []; FailWith = null; LastCredential = null; LastBaseUrl = null; LastOptions = null; LastSystem = null; }

    public ICatalogClient For(ConnectorSystem system) { LastSystem = system; return this; }

    public Task<IReadOnlyList<CatalogCourse>> FetchAsync(ConnectorConfig connector, string credential, IReadOnlyDictionary<string, string> options, HttpClient http, CancellationToken ct)
    {
        LastCredential = credential; LastBaseUrl = connector.BaseUrl; LastOptions = options;
        if (FailWith is { } why) throw new ConnectorException(why);
        return Task.FromResult<IReadOnlyList<CatalogCourse>>(Courses);
    }
}
