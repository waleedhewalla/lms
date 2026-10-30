namespace EduNexus.Api.Events;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMQ";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5673;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    /// <summary>Use TLS (port 5671 on the chart). Certificates are checked against Tls:CaFile when it is set.</summary>
    public bool Tls { get; set; }
    /// <summary>Name the server certificate must carry; defaults to Host.</summary>
    public string? TlsServerName { get; set; }
    public string Exchange { get; set; } = "edunexus.events";
    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 50;
}
