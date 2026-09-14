namespace Ttu.FiberImgApp.Core.Configuration;

/// <summary>
/// Live settings shared by Capture and Settings (updated without restart).
/// </summary>
public sealed class AppSettingsState
{
    public bool UseSimulator { get; set; } = true;
    public string SerialNumber { get; set; } = string.Empty;
    public int Channel { get; set; } = 1;
    public double MinMm { get; set; } = 0;
    public double MaxMm { get; set; } = 100;
    public double VelocityMmPerSec { get; set; } = 5;
    public double AccelerationMmPerSec2 { get; set; } = 10;
    public double JogStepMm { get; set; } = 1;
    public string SessionsRootPath { get; set; } = string.Empty;

    public static string GetDefaultSessionsRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TTU",
            "FiberImgApp",
            "Sessions");

    public string EffectiveSessionsRoot =>
        string.IsNullOrWhiteSpace(SessionsRootPath)
            ? GetDefaultSessionsRoot()
            : SessionsRootPath;

    public void LoadFrom(ThorlabsOptions thorlabs, CaptureOptions capture)
    {
        UseSimulator = thorlabs.UseSimulator;
        SerialNumber = thorlabs.SerialNumber;
        Channel = thorlabs.Channel;
        MinMm = thorlabs.MinMm;
        MaxMm = thorlabs.MaxMm;
        VelocityMmPerSec = thorlabs.VelocityMmPerSec;
        AccelerationMmPerSec2 = thorlabs.AccelerationMmPerSec2;
        JogStepMm = thorlabs.JogStepMm;
        SessionsRootPath = capture.SessionsRootPath;
    }

    public void CopyTo(ThorlabsOptions thorlabs, CaptureOptions capture)
    {
        thorlabs.UseSimulator = UseSimulator;
        thorlabs.SerialNumber = SerialNumber;
        thorlabs.Channel = Channel;
        thorlabs.MinMm = MinMm;
        thorlabs.MaxMm = MaxMm;
        thorlabs.VelocityMmPerSec = VelocityMmPerSec;
        thorlabs.AccelerationMmPerSec2 = AccelerationMmPerSec2;
        thorlabs.JogStepMm = JogStepMm;
        capture.SessionsRootPath = SessionsRootPath;
    }
}