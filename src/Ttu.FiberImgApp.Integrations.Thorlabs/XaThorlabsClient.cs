using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Thorlabs;

/// <summary>
/// Real hardware client for BSC202 + NRT100 via Thorlabs XA .NET SDK.
/// Loads XA assemblies from the Thorlabs install folder at runtime so the app
/// still builds on machines without XA installed.
/// </summary>
public sealed class XaThorlabsClient : IThorlabsClient, IDisposable
{
    private static readonly string[] CandidateRoots =
    [
        @"C:\Program Files\Thorlabs\XA",
        @"C:\Program Files\Thorlabs\Thorlabs XA",
        @"C:\Program Files (x86)\Thorlabs\XA"
    ];

    private readonly ThorlabsOptions _options;
    private readonly ILogger<XaThorlabsClient> _logger;
    private readonly object _gate = new();

    private bool _connected;
    private bool _moving;
    private double _positionMm;
    private string _status = "XA client — SDK not loaded yet";
    private Assembly? _xaAssembly;
    private object? _device;
    private object? _axis;

    public XaThorlabsClient(IOptions<ThorlabsOptions> options, ILogger<XaThorlabsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _positionMm = _options.MinMm;
        TryDiscoverSdk();
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsConnected => _connected;
    public bool IsMoving => _moving;

    public double PositionMm
    {
        get { lock (_gate) return _positionMm; }
    }

    public IReadOnlyList<string> ListDevices()
    {
        if (_xaAssembly is null)
        {
            return Array.Empty<string>();
        }

        // Discovery API varies by XA version; return configured serial as a hint.
        if (!string.IsNullOrWhiteSpace(_options.SerialNumber))
        {
            return new[] { _options.SerialNumber };
        }

        return new[] { "(auto — first BSC202)" };
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_xaAssembly is null && !TryDiscoverSdk())
        {
            SetStatus("Thorlabs XA SDK not found. Install XA, or enable simulator in Settings.");
            return Task.FromResult(false);
        }

        try
        {
            // XA public .NET surface evolves; use documented entry points when available.
            // Until the lab PC confirms exact types, connect is best-effort via reflection.
            var connected = TryConnectViaReflection();
            _connected = connected;
            if (connected)
            {
                SetStatus($"XA connected (ch {_options.Channel})");
                _logger.LogInformation("Connected to Thorlabs XA device channel {Channel}", _options.Channel);
            }
            else
            {
                SetStatus("XA SDK found but connect failed. Check serial/channel and close the XA GUI.");
            }

            return Task.FromResult(connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XA connect failed");
            SetStatus($"XA connect error: {ex.Message}");
            _connected = false;
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            InvokeOptional(_axis, "Disconnect");
            InvokeOptional(_device, "Disconnect");
            InvokeOptional(_device, "Close");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XA disconnect warning");
        }

        _axis = null;
        _device = null;
        _connected = false;
        _moving = false;
        SetStatus("Disconnected");
        return Task.CompletedTask;
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        _moving = true;
        try
        {
            if (!TryInvokeMove("Home", Array.Empty<object?>()))
            {
                await MoveAbsoluteAsync(_options.MinMm, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
            RefreshPosition();
        }
        finally
        {
            _moving = false;
        }
    }

    public async Task MoveAbsoluteAsync(double positionMm, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var clamped = Math.Clamp(positionMm, _options.MinMm, _options.MaxMm);
        _moving = true;
        try
        {
            if (!TryInvokeMove("MoveAbsolute", new object?[] { clamped }) &&
                !TryInvokeMove("MoveTo", new object?[] { clamped }))
            {
                throw new InvalidOperationException(
                    "XA move API not found. Update XaThorlabsClient once SDK types are confirmed on the lab PC.");
            }

            await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
            RefreshPosition();
            lock (_gate) _positionMm = clamped;
        }
        finally
        {
            _moving = false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        TryInvokeMove("Stop", Array.Empty<object?>());
        _moving = false;
        return Task.CompletedTask;
    }

    public Task ApplyMotionParametersAsync(
        double velocityMmPerSec,
        double accelerationMmPerSec2,
        CancellationToken cancellationToken = default)
    {
        _options.VelocityMmPerSec = velocityMmPerSec;
        _options.AccelerationMmPerSec2 = accelerationMmPerSec2;
        TryInvokeMove("SetVelocity", new object?[] { velocityMmPerSec });
        TryInvokeMove("SetAcceleration", new object?[] { accelerationMmPerSec2 });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
    }

    private bool TryDiscoverSdk()
    {
        foreach (var root in CandidateRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var dll = Directory.EnumerateFiles(root, "*XA*.dll", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*Thorlabs*Motion*.dll", SearchOption.AllDirectories))
                .FirstOrDefault(p => p.Contains("XA", StringComparison.OrdinalIgnoreCase)
                                     || p.Contains("MotionControl", StringComparison.OrdinalIgnoreCase));

            if (dll is null)
            {
                continue;
            }

            try
            {
                _xaAssembly = Assembly.LoadFrom(dll);
                SetStatus($"XA SDK loaded from {dll}");
                _logger.LogInformation("Loaded XA assembly {Path}", dll);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed loading {Dll}", dll);
            }
        }

        SetStatus("Thorlabs XA SDK not installed (simulator recommended until XA is set up)");
        return false;
    }

    private bool TryConnectViaReflection()
    {
        if (_xaAssembly is null)
        {
            return false;
        }

        // Prefer types whose names suggest device / axis controllers.
        var candidates = _xaAssembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Name.Contains("Device", StringComparison.OrdinalIgnoreCase)
                        || t.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                        || t.Name.Contains("Axis", StringComparison.OrdinalIgnoreCase)
                        || t.Name.Contains("Stepper", StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        _logger.LogInformation(
            "XA types available (sample): {Types}",
            string.Join(", ", candidates.Select(t => t.FullName)));

        // Without vendor docs on this machine we cannot safely construct devices.
        // Fail clearly so Settings can fall back to simulator.
        SetStatus(
            "XA SDK detected but binding needs lab confirmation. " +
            "Set Thorlabs:UseSimulator=true for now, or finish XaThorlabsClient with your installed SDK examples.");
        return false;
    }

    private bool TryInvokeMove(string methodName, object?[] args)
    {
        var target = _axis ?? _device;
        if (target is null)
        {
            return false;
        }

        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                                 && m.GetParameters().Length == args.Length);
        if (method is null)
        {
            return false;
        }

        method.Invoke(target, args);
        return true;
    }

    private static void InvokeOptional(object? target, string methodName)
    {
        if (target is null)
        {
            return;
        }

        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method?.Invoke(target, null);
    }

    private async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 600; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDeviceMoving())
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsDeviceMoving()
    {
        var target = _axis ?? _device;
        if (target is null)
        {
            return false;
        }

        var prop = target.GetType().GetProperty("IsMoving") ?? target.GetType().GetProperty("Moving");
        if (prop?.GetValue(target) is bool moving)
        {
            return moving;
        }

        return false;
    }

    private void RefreshPosition()
    {
        var target = _axis ?? _device;
        if (target is null)
        {
            return;
        }

        var prop = target.GetType().GetProperty("Position")
                   ?? target.GetType().GetProperty("PositionMm");
        if (prop?.GetValue(target) is double d)
        {
            lock (_gate) _positionMm = d;
        }
        else if (prop?.GetValue(target) is float f)
        {
            lock (_gate) _positionMm = f;
        }
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Stage is not connected.");
        }
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
    }
}
