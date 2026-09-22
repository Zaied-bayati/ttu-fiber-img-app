using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Zen;

internal static class ZenChannelFactory
{
    public const int DefaultGatewayPort = 5002;

    /// <summary>
    /// Axiocam 820 mono full frame is about 41 MB (16-bit) and a color frame can exceed 120 MB.
    /// Grpc.Net.Client defaults to 4 MB, which drops those frames before the app can display them.
    /// </summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    private static readonly string[] TokenFiles =
    [
        @"C:\ProgramData\Carl Zeiss\ZEN APIGateway\GlobalControlToken.txt",
        @"C:\ProgramData\Carl Zeiss\ZenApiGateway\GlobalControlToken.txt",
    ];

    private static readonly string[] CertificateFiles =
    [
        @"C:\ProgramData\Carl Zeiss\ZenApiGateway\Certificates\ZenApiPersonalSigningRootCA.pem",
        @"C:\ProgramData\Carl Zeiss\ZEN APIGateway\Certificates\ZenApiPersonalSigningRootCA.pem",
        @"C:\ProgramData\Carl Zeiss\ZEN APIGateway\Certificates\ZEN APIPersonalSigningRootCA.pem",
    ];

    public static IReadOnlyList<int> CandidatePorts(int configured)
    {
        var ports = new List<int>();
        Add(configured);
        Add(DefaultGatewayPort);
        Add(50051);
        Add(5000);
        return ports;

        void Add(int port)
        {
            if (port is > 0 and <= 65535 && !ports.Contains(port))
                ports.Add(port);
        }
    }

    public static GrpcChannel Create(ZenOptions options, int port, out string transportNote)
    {
        var callback = BuildValidation(options, out transportNote);
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(options.Host) ? "localhost" : options.Host,
                RemoteCertificateValidationCallback = callback,
            },
        };

        return GrpcChannel.ForAddress($"https://{options.Host}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = MaxFrameBytes,
            MaxSendMessageSize = 8 * 1024 * 1024,
        });
    }

    public static string ResolveToken(ZenOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiToken))
            return Clean(options.ApiToken);

        if (!string.IsNullOrWhiteSpace(options.ApiTokenFilePath) && File.Exists(options.ApiTokenFilePath))
            return Clean(File.ReadAllText(options.ApiTokenFilePath));

        foreach (var candidate in TokenFiles)
        {
            if (File.Exists(candidate))
                return Clean(File.ReadAllText(candidate));
        }

        return string.Empty;
    }

    private static RemoteCertificateValidationCallback BuildValidation(ZenOptions options, out string transportNote)
    {
        if (options.AllowUntrustedCertificate)
        {
            transportNote = "TLS certificate checks disabled";
            return static (_, _, _, _) => true;
        }

        var path = ResolveCertificatePath(options);
        X509Certificate2? ca = null;
        if (path is not null)
        {
            try
            {
                ca = X509CertificateLoader.LoadCertificateFromFile(path);
            }
            catch
            {
                ca = null;
            }
        }

        var loopback = IsLoopback(options.Host);
        if (ca is null)
        {
            if (loopback)
            {
                transportNote = "gateway CA file not found; accepting the localhost certificate";
                return static (_, _, _, _) => true;
            }

            transportNote = "using the system trust store";
            return static (_, cert, _, errors) => cert is not null && errors == SslPolicyErrors.None;
        }

        var authority = ca;
        transportNote = $"trusting gateway CA {path}";
        return (_, cert, _, errors) =>
        {
            if (cert is null) return false;
            if (errors == SslPolicyErrors.None) return true;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain.Build(new X509Certificate2(cert)))
                return true;

            // Lab gateways are self-signed for the PC name while the app connects to localhost.
            return loopback;
        };
    }

    private static string? ResolveCertificatePath(ZenOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificatePath) && File.Exists(options.CertificatePath))
            return options.CertificatePath;

        foreach (var candidate in CertificateFiles)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsLoopback(string host) =>
        string.IsNullOrWhiteSpace(host)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string token) => token.Trim().Trim('\uFEFF');
}
