using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KCMundial.Core.Interfaces;
using KCMundial.ShareServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace KCMundial.App.Services;

public sealed class LocalServerHost
{
    private WebApplication? _app;
    private readonly IPathResolver _pathResolver;
    private readonly IAppLogger? _logger;

    public int Port { get; private set; }
    public string? BaseUrl { get; private set; }

    public LocalServerHost(IPathResolver pathResolver, IAppLogger? logger = null)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        Port = GetAvailablePort();
        var ip = GetLocalIpAddress();
        var cert = CreateSelfSignedCertificate(ip);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, Port, listenOptions =>
            {
                listenOptions.UseHttps(cert);
            });
        });
        _app = builder.Build();
        _app.MapShareRoutes(_pathResolver);
        await _app.StartAsync();
        BaseUrl = $"https://{ip}:{Port}";
        _logger?.Info($"Share server started: {BaseUrl} (port {Port}, HTTPS for Compartir/Descargar en el celular)");
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string localIp)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "cn=KCMundial",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        if (IPAddress.TryParse(localIp, out var ipAddr))
            san.AddIpAddress(ipAddr);
        request.CertificateExtensions.Add(san.Build());
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        const string pwd = "KCMundialLocal";
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, pwd), pwd, X509KeyStorageFlags.Exportable);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            try
            {
                _logger?.Info("Stopping share server...");
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
                _logger?.Info("Share server stopped");
            }
            catch (Exception ex)
            {
                _logger?.Error("Error stopping server", ex);
                _app = null;
            }
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { /* ignore */ }
        return "127.0.0.1";
    }
}
