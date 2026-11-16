using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Plugins.Web.Tests;

/// <summary>
/// docs/architecture.md §7.16–7.19 (in NKeeper): the web plugin, packaged with YARP and loaded like any plugin by
/// NKeeper's test server, proxying a real HTTPS server with a self-signed certificate, on a session origin of its own.
/// </summary>
public sealed class WebTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private const string WebDomain = "web.test";
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_opens_once_its_certificate_is_trusted_and_is_proxied_on_its_own_origin()
    {
        await using var target = await TargetServer.StartAsync();
        await using var running = await ServerAsync();
        var server = running.Server;
        using var admin = await server.SignedInClientAsync(Token);
        var host = await CreateHostAsync(admin, target.Port);

        // Not publicly valid and not trusted: core refuses, and shows the certificate.
        using var refused = await OpenAsync(admin, host);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("host_key_unknown", problem.GetProperty("code").GetString());
        var certificate = problem.GetProperty("presented");
        Assert.Equal(("tls-certificate", "x509"), (certificate.GetProperty("kind").GetString(), certificate.GetProperty("algorithm").GetString()));
        Assert.Equal(target.Fingerprint, certificate.GetProperty("fingerprint").GetString());
        Assert.Equal("CN=nkeeper-web-target", certificate.GetProperty("subject").GetString());

        using var trusted = await admin.SendAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys", new { algorithm = "x509", fingerprint = target.Fingerprint }, Token);
        Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        var key = (await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{Id(host)}", UriKind.Relative), Token)).GetProperty("trustedKeys")[0];
        Assert.Equal(("tls-certificate", "x509"), (key.GetProperty("kind").GetString(), key.GetProperty("algorithm").GetString()));

        // The TLS health probe reports the certificate, trusted or not.
        using var checkedNow = await admin.SendAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/health", cancellationToken: Token);
        var health = (await checkedNow.Content.ReadFromJsonAsync<JsonElement>(Token))[0];
        Assert.Equal(("up", "CN=nkeeper-web-target"), (health.GetProperty("state").GetString(), health.GetProperty("certificateSubject").GetString()));
        Assert.True(health.GetProperty("certificateExpiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));

        using var opened = await OpenAsync(admin, host);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync(Token));
        var session = await opened.Content.ReadFromJsonAsync<JsonElement>(Token);
        var launch = new Uri(session.GetProperty("url").GetString()!);
        Assert.Equal($"nkw-{session.GetProperty("id").GetString()}.{WebDomain}", launch.Host);

        // The launch link sets the session's cookie, once.
        using var origin = running.OriginClient();
        using var launched = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        Assert.Equal(HttpStatusCode.Redirect, launched.StatusCode);
        var cookie = launched.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__nkw_session=", StringComparison.Ordinal)).Split(';')[0];
        using var again = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        // With the cookie, the target answers through the proxy; its cookie stays on the session origin.
        using var page = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("hello from the target", await page.Content.ReadAsStringAsync(Token));
        Assert.Contains(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith("target=1", StringComparison.Ordinal) && c.Contains("SameSite=None", StringComparison.Ordinal));
        Assert.Contains(page.Headers.GetValues("Content-Security-Policy"), p => p == $"frame-ancestors {server.BaseAddress.GetLeftPart(UriPartial.Authority)}");

        // An absolute redirect to the target stays on the session origin.
        using var moved = await OriginAsync(origin, launch.Host, "/moved", cookie);
        Assert.Equal($"http://{launch.Host}/", moved.Headers.Location!.ToString());

        // Without the cookie, nothing is proxied.
        using var stranger = await OriginAsync(origin, launch.Host, "/", cookie: null);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);

        // Closing it ends it.
        using var closed = await admin.SendAsync(HttpMethod.Delete, $"/api/p/web/hosts/{Id(host)}/sessions/{session.GetProperty("id").GetString()}", cancellationToken: Token);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        using var gone = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        var events = await running.AuditAsync("action LIKE 'session.%' OR action = 'trust.add'");
        Assert.Equal(["trust.add", "session.open", "session.close"], events.Select(e => e.Action));
    }

    [Fact]
    public async Task A_page_ends_when_access_to_its_host_ends()
    {
        await using var target = await TargetServer.StartAsync();
        await using var running = await ServerAsync();
        var server = running.Server;
        using var admin = await server.SignedInClientAsync(Token);
        var host = await CreateHostAsync(admin, target.Port);
        // Core trusts only a certificate it presented: the administrator's first attempt presents it.
        (await OpenAsync(admin, host)).Dispose();
        (await admin.SendAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys", new { algorithm = "x509", fingerprint = target.Fingerprint }, Token)).EnsureSuccessStatusCode();
        var bobId = await CreateUserAsync(admin, "bob", BobPassword);
        (await admin.SendAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(host), level = "use" } }, Token)).EnsureSuccessStatusCode();
        using var bob = server.CreateClient();
        (await bob.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = "bob", password = BobPassword, rememberMe = false }, Token)).EnsureSuccessStatusCode();

        // The certificate was trusted for the host, so bob's page opens without asking.
        using var opened = await OpenAsync(bob, host);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync(Token));
        var launch = new Uri((await opened.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("url").GetString()!);
        using var origin = running.OriginClient();
        using var launched = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        var cookie = launched.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__nkw_session=", StringComparison.Ordinal)).Split(';')[0];
        using var page = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        (await admin.SendAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", Array.Empty<object>(), Token)).EnsureSuccessStatusCode();
        using var after = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);

        var closes = await running.AuditAsync("action = 'session.close'");
        Assert.Contains(closes, e => e.Detail.Contains(": access_revoked,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Through_a_jump_host_a_page_needs_the_jump_hosts_key_trusted_first()
    {
        await using var running = await ServerAsync();
        using var admin = await running.Server.SignedInClientAsync(Token);

        // Any service will do for a jump host: core only connects to its address and port. The plugin's own http
        // type stands in, since no other plugin is loaded here.
        using var createdJump = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "jump",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { type = "http", port = ssh.Port, settings = new { } } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        }, Token);
        Assert.Equal(HttpStatusCode.Created, createdJump.StatusCode);
        var jump = await createdJump.Content.ReadFromJsonAsync<JsonElement>(Token);
        using var createdHost = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "behind",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "https", port = 9, settings = new { } } },
            jump = new { hostId = Id(jump), serviceId = ServiceId(jump), credentialId = jump.GetProperty("credentials")[0].GetProperty("id").GetGuid() },
        }, Token);
        Assert.Equal(HttpStatusCode.Created, createdHost.StatusCode);
        var host = await createdHost.Content.ReadFromJsonAsync<JsonElement>(Token);

        // A request can't stop to ask about the jump host's key.
        using var untrusted = await OpenAsync(admin, host);
        var refusal = await untrusted.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal((HttpStatusCode.BadGateway, "jump_host_unavailable"), (untrusted.StatusCode, refusal.GetProperty("code").GetString()));
        Assert.Contains("isn't trusted yet", refusal.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_web_domain_pages_say_so()
    {
        await using var running = await ServerAsync(webDomain: false);
        using var admin = await running.Server.SignedInClientAsync(Token);
        var host = await CreateHostAsync(admin, 443);

        using var refused = await OpenAsync(admin, host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("web_domain_missing", (await refused.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
    }

    private static Task<HttpResponseMessage> OpenAsync(TestServerClient client, JsonElement host) =>
        client.SendAsync(HttpMethod.Post, $"/api/p/web/hosts/{Id(host)}/sessions", new { serviceId = ServiceId(host) }, Token);

    /// <summary>A request on a session origin: the server answers any host, so the Host header picks it.</summary>
    private static async Task<HttpResponseMessage> OriginAsync(HttpClient client, string originHost, string pathAndQuery, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(pathAndQuery, UriKind.Relative));
        request.Headers.Host = originHost;
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, Token);
    }

    private static async Task<JsonElement> CreateHostAsync(TestServerClient admin, int port)
    {
        using var response = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "https", port, settings = new { } } },
        }, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<Guid> CreateUserAsync(TestServerClient admin, string userName, string password)
    {
        using var response = await admin.SendAsync(HttpMethod.Post, "/api/users", new { userName, email = $"{userName}@example.com", role = "user", password, mustChangePassword = false }, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    /// <summary>NKeeper's test server, running with this plugin's package loaded, and its database.</summary>
    private async Task<RunningServer> ServerAsync(bool webDomain = true)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = connectionString };
        if (webDomain)
        {
            options.Settings["Origins:BaseDomain"] = WebDomain;
        }

        // The package this repository's build made: the test project builds the plugin first.
        var packages = DevelopmentKeys.BuiltPackages().Where(p => Path.GetFileName(p).StartsWith("web-", StringComparison.Ordinal)).ToList();
        Assert.Single(packages);
        options.PluginPackages.Add(packages[0]);
        return new RunningServer(await TestServer.StartAsync(options, Token), connectionString);
    }

    private sealed class RunningServer(TestServer server, string connectionString) : IAsyncDisposable
    {
        public TestServer Server { get; } = server;

        /// <summary>A client that keeps no cookies and follows no redirects, for session origins.</summary>
        public HttpClient OriginClient() =>
            new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = Server.BaseAddress };

        /// <summary>The audit events matching a SQL condition, in the order they happened. The server is its own process, so this reads its database.</summary>
        public async Task<IReadOnlyList<(string Action, string Detail)>> AuditAsync(string condition)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Token);
            await using var command = new NpgsqlCommand($"SELECT action, detail FROM core.audit_events WHERE {condition} ORDER BY id", connection);
            await using var reader = await command.ExecuteReaderAsync(Token);
            var events = new List<(string, string)>();
            while (await reader.ReadAsync(Token))
            {
                events.Add((reader.GetString(0), reader.GetString(1)));
            }

            return events;
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    /// <summary>A real HTTPS server on loopback, with a self-signed certificate: the site behind the proxy.</summary>
    private sealed class TargetServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TargetServer(WebApplication app, int port, string fingerprint)
        {
            _app = app;
            Port = port;
            Fingerprint = fingerprint;
        }

        public int Port { get; }

        public string Fingerprint { get; }

        public static async Task<TargetServer> StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=nkeeper-web-target", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var sans = new SubjectAlternativeNameBuilder();
            sans.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sans.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            var port = 0;
            app.MapGet("/", (HttpContext context) =>
            {
                context.Response.Cookies.Append("target", "1");
                return Results.Text("hello from the target");
            });
            app.MapGet("/moved", (HttpContext context) => Results.Redirect($"https://127.0.0.1:{port}/"));
            await app.StartAsync();
            port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            return new TargetServer(app, port, Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('='));
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
