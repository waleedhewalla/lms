using EduNexus.Api.Tls;
using RabbitMQ.Client;

namespace EduNexus.Api.Events;

/// <summary>The one place a RabbitMQ connection is described, so publisher and consumers cannot drift apart on TLS.</summary>
public static class RabbitMqConnection
{
    public static ConnectionFactory Factory(RabbitMqOptions cfg, PrivateCa? ca)
    {
        var factory = new ConnectionFactory
        {
            HostName = cfg.Host,
            Port = cfg.Port,
            UserName = cfg.Username,
            Password = cfg.Password,
            AutomaticRecoveryEnabled = true,
        };
        if (cfg.Tls)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = string.IsNullOrWhiteSpace(cfg.TlsServerName) ? cfg.Host : cfg.TlsServerName,
            };
            // Verify against the site CA when one is configured; otherwise the system store, as for any TLS service.
            if (ca is not null) factory.Ssl.CertificateValidationCallback = ca.Callback();
        }
        return factory;
    }
}
