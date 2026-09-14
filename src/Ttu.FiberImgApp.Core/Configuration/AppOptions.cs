namespace Ttu.FiberImgApp.Core.Configuration;

public sealed class ZenOptions
{
    public const string SectionName = "Zen";

    /// <summary>When true, use PlaceholderCameraService (no ZEN Gateway required).</summary>
    public bool UseSimulator { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50051;
    public string ApiToken { get; set; } = string.Empty;
    public string? ApiTokenFilePath { get; set; }

    /// <summary>ZEN experiment name without .czexp extension (Axiocam 820 live/snap setup).</summary>
    public string ExperimentName { get; set; } = string.Empty;

    /// <summary>Optional path to Gateway CA certificate (.pem/.crt). Empty = use system trust store.</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Channel index for mono streaming (typically 0).</summary>
    public int ChannelIndex { get; set; } = 0;

    /// <summary>When true, skip TLS certificate validation (lab debugging only).</summary>
    public bool AllowUntrustedCertificate { get; set; } = false;
}

public sealed class ThorlabsOptions
{
    public const string SectionName = "Thorlabs";

    /// <summary>When true, use SimulatedThorlabsClient (no hardware / XA SDK required).</summary>
    public bool UseSimulator { get; set; } = true;

    /// <summary>Controller serial number or device id. Empty = first discovered / simulator default.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Legacy alias used by older stubs.</summary>
    public string DeviceId
    {
        get => SerialNumber;
        set => SerialNumber = value;
    }

    /// <summary>BSC202 channel that drives the NRT100 (typically 1).</summary>
    public int Channel { get; set; } = 1;

    public double MinMm { get; set; } = 0;
    public double MaxMm { get; set; } = 100;
    public double VelocityMmPerSec { get; set; } = 5;
    public double AccelerationMmPerSec2 { get; set; } = 10;
    public double JogStepMm { get; set; } = 1;
}

public sealed class CaptureOptions
{
    public const string SectionName = "Capture";

    /// <summary>Empty = Documents\TTU\FiberImgApp\Sessions</summary>
    public string SessionsRootPath { get; set; } = string.Empty;
}

public sealed class UpdatesOptions
{
    public const string SectionName = "Updates";

    public string AppInstallerUri { get; set; } =
        "https://github.com/Zaied-bayati/ttu-fiber-img-app/releases/latest/download/";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = string.Empty;
}

public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    public string MinimumLevel { get; set; } = "Information";
}
