using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Zen;

internal static class ZenChannelFactory
{
    public static GrpcChannel Create(ZenOptions options)
    {
        var address = $"https://{options.Host}:{options.Port}";
        var handler = new HttpClientHandler();

        if (options.AllowUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (!string.IsNullOrWhiteSpace(options.CertificatePath) && File.Exists(options.CertificatePath))
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(options.CertificatePath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null) return false;
                if (errors == SslPolicyErrors.None) return true;
                using var chain = new X509Chain();
                chain.ChainPolicy.ExtraStore.Add(ca);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            };
        }

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    public static string ResolveToken(ZenOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiToken))
            return options.ApiToken.Trim();

        if (!string.IsNullOrWhiteSpace(options.ApiTokenFilePath) && File.Exists(options.ApiTokenFilePath))
            return File.ReadAllText(options.ApiTokenFilePath).Trim();

        return string.Empty;
    }
}