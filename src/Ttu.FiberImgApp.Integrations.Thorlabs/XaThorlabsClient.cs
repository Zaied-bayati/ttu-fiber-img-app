using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Thorlabs;

/// <summary>
/// Real hardware client for BSC20x + NRT100 via Thorlabs XA .NET SDK.
/// Loads XA assemblies from the Thorlabs install folder at runtime so the app
/// still builds on machines without XA installed.
/// </summary>
public sealed class XaThorlabsClient : IThorlabsClient, IDisposable
{
    private const string ConnectedProductName = "NRT100";

    private static readonly string[] CandidateDlls =
    [
        @"C:\Program Files\Thorlabs XA\SDK\.NET Framework (C#)\Libraries\x64\tlmc_xa_dotnet.dll",
        @"C:\Program Files\Thorlabs XA\tlmc_xa_dotnet.dll",
        @"C:\Program Files\Thorlabs\XA\SDK\.NET Framework (C#)\Libraries\x64\tlmc_xa_dotnet.dll",
        @"C:\Program Files\Thorlabs\XA\tlmc_xa_dotnet.dll",
        @"C:\Program Files\Thorlabs\Thorlabs XA\tlmc_xa_dotnet.dll",
        @"C:\Program Files (x86)\Thorlabs XA\tlmc_xa_dotnet.dll",
        @"C:\Program Files (x86)\Thorlabs\XA\tlmc_xa_dotnet.dll"
    ];

    private static readonly string[] CandidateRoots =
    [
        @"C:\Program Files\Thorlabs XA",
        @"C:\Program Files\Thorlabs\XA",
        @"C:\Program Files\Thorlabs\Thorlabs XA",
        @"C:\Program Files (x86)\Thorlabs XA",
        @"C:\Program Files (x86)\Thorlabs\XA"
    ];

    private readonly ThorlabsOptions _options;
    private readonly ILogger<XaThorlabsClient> _logger;
    private readonly IActivityLog _activityLog;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sdkLock = new(1, 1);

    private bool _connected;
    private bool _moving;
    private double _positionMm;
    private string _status = "XA client — SDK not loaded yet";
    private Assembly? _xaAssembly;
    private object? _systemManager;
    private object? _channel;
    private string? _channelDeviceId;
    private string? _loadedDllPath;

    public XaThorlabsClient(IOptions<ThorlabsOptions> options, ILogger<XaThorlabsClient> logger, IActivityLog activityLog)
    {
        _options = options.Value;
        _logger = logger;
        _activityLog = activityLog;
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
        if (_xaAssembly is null && !TryDiscoverSdk())
        {
            return Array.Empty<string>();
        }

        try
        {
            EnsureSystemStarted();
            var infos = GetDeviceInfos();
            var channels = infos
                .Where(IsLogicalChannel)
                .Select(DescribeDevice)
                .ToList();

            if (channels.Count > 0)
            {
                return channels;
            }

            return infos.Select(DescribeDevice).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XA ListDevices failed");
            return string.IsNullOrWhiteSpace(_options.SerialNumber)
                ? new[] { "(auto — first BSC20x channel)" }
                : new[] { _options.SerialNumber };
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return await RunSdkAsync(ConnectCore, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await RunSdkAsync(DisconnectCore, cancellationToken).ConfigureAwait(false);
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        _moving = true;
        try
        {
            SetStatus("Homing...");
            // Timeout.Zero starts the home without blocking this thread so Cancel/Stop can run.
            await RunSdkAsync(() =>
            {
                Invoke(_channel!, "Home", GetTimeoutZero());
            }, cancellationToken).ConfigureAwait(false);

            await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
            await RefreshPositionAsync(cancellationToken).ConfigureAwait(false);
            SetStatus($"Homed @ {PositionMm:0.###} mm");
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
            await RunSdkAsync(() =>
            {
                var deviceUnits = ToDeviceUnits(_channel!, ScaleDistance(), UnitMillimetres(), clamped);
                SetStatus($"Moving to {clamped:0.###} mm (device units {deviceUnits})...");
                var moveMode = GetEnumValue("Thorlabs.MotionControl.XA.MoveMode", "Absolute");
                Invoke(_channel!, "Move", moveMode, checked((int)deviceUnits), GetTimeoutZero());
            }, cancellationToken).ConfigureAwait(false);

            await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
            await RefreshPositionAsync(cancellationToken).ConfigureAwait(false);
            SetStatus($"At {PositionMm:0.###} mm");
        }
        finally
        {
            _moving = false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await RunSdkAsync(() =>
        {
            if (_channel is not null)
            {
                var immediate = GetEnumValue("Thorlabs.MotionControl.XA.StopMode", "Immediate");
                TryInvoke(_channel, "Stop", immediate, GetTimeoutZero());
            }

            _moving = false;
            SetStatus("Stop requested");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyMotionParametersAsync(
        double velocityMmPerSec,
        double accelerationMmPerSec2,
        CancellationToken cancellationToken = default)
    {
        _options.VelocityMmPerSec = velocityMmPerSec;
        _options.AccelerationMmPerSec2 = accelerationMmPerSec2;
        await RunSdkAsync(() =>
        {
            if (_channel is not null)
            {
                ApplyMotionParametersCore(_channel, velocityMmPerSec, accelerationMmPerSec2);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshPositionAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected || _channel is null)
        {
            return;
        }

        await RunSdkAsync(RefreshPosition, cancellationToken).ConfigureAwait(false);
    }

    private bool ConnectCore()
    {
        if (_xaAssembly is null && !TryDiscoverSdk())
        {
            SetStatus("Thorlabs XA SDK not found. Install XA, or enable simulator in Settings.");
            return false;
        }

        try
        {
            EnsureSystemStarted();
            var infos = GetDeviceInfos();
            LogDeviceList(infos);

            var channelInfo = SelectChannel(infos);
            if (channelInfo is null)
            {
                SetStatus(
                    "No BSC20x channel found. Power the controller, close the XA GUI, then retry. " +
                    "Seen: " + string.Join("; ", infos.Select(DescribeDevice)));
                return false;
            }

            var deviceId = GetStringProp(channelInfo, "Device");
            var transport = GetStringProp(channelInfo, "Transport") ?? string.Empty;
            SetStatus($"Opening XA channel {deviceId} (settings ch {_options.Channel})...");

            if (!TryOpenDevice(deviceId!, transport, out var channel) || channel is null)
            {
                SetStatus($"XA open failed for device {deviceId}. Close XA GUI and confirm USB.");
                return false;
            }

            _channel = channel;
            _channelDeviceId = deviceId;

            TrySetConnectedProduct(channel, ConnectedProductName);
            TryEnable(channel);
            ApplyMotionParametersCore(channel, _options.VelocityMmPerSec, _options.AccelerationMmPerSec2);
            RefreshPosition();

            _connected = true;
            SetStatus($"XA connected — {DescribeDevice(channelInfo)} @ {PositionMm:0.###} mm ({ConnectedProductName})");
            _logger.LogInformation("Connected to Thorlabs XA channel {DeviceId}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XA connect failed");
            SafeCloseChannel();
            SetStatus($"XA connect error: {Flatten(ex)}");
            _connected = false;
            return false;
        }
    }

    private void DisconnectCore()
    {
        try
        {
            if (_channel is not null)
            {
                TryInvoke(_channel, "Disconnect");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XA disconnect warning");
        }

        SafeCloseChannel();
        _connected = false;
        _moving = false;
        SetStatus("Disconnected");
    }

    private async Task RunSdkAsync(Action action, CancellationToken cancellationToken)
    {
        await _sdkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    private async Task<T> RunSdkAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        await _sdkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(func, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            DisconnectCore();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_systemManager is not null)
            {
                TryInvoke(_systemManager, "Shutdown");
                TryInvoke(_systemManager, "Dispose");
            }
        }
        catch
        {
            // ignore
        }

        _systemManager = null;
        _sdkLock.Dispose();
    }

    private bool TryDiscoverSdk()
    {
        foreach (var dll in CandidateDlls.Where(File.Exists))
        {
            if (TryLoadAssembly(dll))
            {
                return true;
            }
        }

        foreach (var root in CandidateRoots.Where(Directory.Exists))
        {
            var matches = Directory.EnumerateFiles(root, "tlmc_xa_dotnet.dll", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains(@"\x64\", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Length);

            foreach (var dll in matches)
            {
                if (TryLoadAssembly(dll))
                {
                    return true;
                }
            }
        }

        SetStatus("Thorlabs XA SDK not installed (simulator recommended until XA is set up)");
        return false;
    }

    private bool TryLoadAssembly(string dll)
    {
        try
        {
            _xaAssembly = Assembly.LoadFrom(dll);
            _loadedDllPath = dll;
            SetStatus($"XA SDK loaded from {dll}");
            _logger.LogInformation("Loaded XA assembly {Path}", dll);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading {Dll}", dll);
            return false;
        }
    }

    private void EnsureSystemStarted()
    {
        if (_systemManager is not null)
        {
            return;
        }

        var smType = RequireType("Thorlabs.MotionControl.XA.SystemManager");
        _systemManager = smType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)
            ?? throw new InvalidOperationException("SystemManager.Create returned null.");

        var startup = smType.GetMethod("Startup", Type.EmptyTypes)
                      ?? throw new InvalidOperationException("SystemManager.Startup not found.");
        startup.Invoke(_systemManager, null);
        SetStatus("XA SystemManager started — scanning USB...");
    }

    private List<object> GetDeviceInfos()
    {
        EnsureSystemStarted();
        var list = Invoke(_systemManager!, "GetDeviceList") as IEnumerable
                   ?? Array.Empty<object>();
        return list.Cast<object>().ToList();
    }

    private object? SelectChannel(IReadOnlyList<object> infos)
    {
        var channels = infos.Where(IsLogicalChannel).ToList();
        if (channels.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.SerialNumber))
        {
            var serial = _options.SerialNumber.Trim();
            channels = channels.Where(c =>
            {
                var device = GetStringProp(c, "Device") ?? string.Empty;
                var parent = GetStringProp(c, "ParentDevice") ?? string.Empty;
                var transport = GetStringProp(c, "Transport") ?? string.Empty;
                return device.Contains(serial, StringComparison.OrdinalIgnoreCase)
                       || parent.Contains(serial, StringComparison.OrdinalIgnoreCase)
                       || transport.Contains(serial, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (channels.Count == 0)
            {
                SetStatus($"No channel matched SerialNumber '{serial}'.");
                return null;
            }
        }

        channels = channels
            .OrderBy(c => GetStringProp(c, "ParentDevice") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => GetStringProp(c, "Device") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = Math.Clamp(_options.Channel, 1, channels.Count) - 1;
        return channels[index];
    }

    private bool TryOpenDevice(string deviceId, string transport, out object? device)
    {
        device = null;
        var smType = _systemManager!.GetType();
        var modesType = RequireType("Thorlabs.MotionControl.XA.OperatingModes");
        var defaultMode = Enum.ToObject(modesType, 256); // OperatingModes.Default

        var tryOpen = smType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "TryOpenDevice"
                && !m.IsGenericMethod
                && m.GetParameters().Length == 4);

        if (tryOpen is null)
        {
            // Fallback to OpenDevice
            var open = smType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "OpenDevice" && !m.IsGenericMethod && m.GetParameters().Length == 3);
            device = open.Invoke(_systemManager, new[] { deviceId, transport ?? string.Empty, defaultMode });
            return device is not null;
        }

        var args = new object?[] { deviceId, transport ?? string.Empty, defaultMode, null };
        var ok = (bool)(tryOpen.Invoke(_systemManager, args) ?? false);
        device = args[3];
        return ok && device is not null;
    }

    private void TrySetConnectedProduct(object channel, string productName)
    {
        try
        {
            var supported = Invoke(channel, "GetSupportedConnectedProducts") as IEnumerable;
            var names = supported?.Cast<object>().Select(o => o?.ToString() ?? string.Empty).ToList()
                        ?? new List<string>();
            if (names.Count > 0 && !names.Any(n => n.Equals(productName, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"Connected product '{productName}' not in XA list; continuing with controller defaults.");
                return;
            }

            Invoke(channel, "SetConnectedProduct", productName);
            SetStatus($"Connected product set to {productName}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetConnectedProduct({Product}) failed", productName);
            SetStatus($"SetConnectedProduct({productName}) warning: {Flatten(ex)}");
        }
    }

    private void TryEnable(object channel)
    {
        var enableType = RequireType("Thorlabs.MotionControl.XA.EnableState");
        var enabled = Enum.ToObject(enableType, 1); // Enabled
        var timeout = TimeSpan.FromSeconds(5);
        try
        {
            Invoke(channel, "SetEnableState", enabled, timeout);
        }
        catch (Exception ex)
        {
            // Already-enabled controllers sometimes reject a redundant set; keep going if status says enabled.
            _logger.LogWarning(ex, "SetEnableState failed; checking current state");
            try
            {
                var state = Invoke(channel, "GetEnableState", timeout);
                if (state?.ToString()?.Contains("Enabled", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return;
                }
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    private void ApplyMotionParametersCore(object channel, double velocityMmPerSec, double accelerationMmPerSec2)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(2);
            var parameters = Invoke(channel, "GetVelocityParams", timeout)
                             ?? throw new InvalidOperationException("GetVelocityParams returned null.");

            var velUnits = ToDeviceUnits(channel, ScaleVelocity(), UnitMillimetres(), velocityMmPerSec);
            var accUnits = ToDeviceUnits(channel, ScaleAcceleration(), UnitMillimetres(), accelerationMmPerSec2);

            SetProp(parameters, "MinVelocity", 0L);
            SetProp(parameters, "MaxVelocity", velUnits);
            SetProp(parameters, "Acceleration", accUnits);
            Invoke(channel, "SetVelocityParams", parameters);
            SetStatus($"Velocity={velocityMmPerSec:0.###} mm/s Accel={accelerationMmPerSec2:0.###} mm/s²");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply velocity/accel failed");
            SetStatus($"Velocity apply warning: {Flatten(ex)}");
        }
    }

    private long ToDeviceUnits(object channel, object scaleType, object unit, double physical)
    {
        var value = Invoke(channel, "FromPhysicalToDeviceUnit", scaleType, unit, physical);
        return Convert.ToInt64(value);
    }

    private void RefreshPosition()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(2);
            var counter = Convert.ToInt64(Invoke(_channel, "GetPositionCounter", timeout) ?? 0L);
            var converted = Invoke(_channel, "FromDeviceUnitToPhysical", ScaleDistance(), counter);
            var physical = GetProp(converted!, "Value");
            if (physical is double d)
            {
                lock (_gate) _positionMm = d;
            }
            else if (physical is float f)
            {
                lock (_gate) _positionMm = f;
            }
            else if (physical is not null)
            {
                lock (_gate) _positionMm = Convert.ToDouble(physical);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RefreshPosition failed");
        }
    }

    private async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2400; i++) // ~2 minutes at 50ms
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moving = await RunSdkAsync(() =>
            {
                RefreshPosition();
                return IsDeviceMoving();
            }, cancellationToken).ConfigureAwait(false);

            if (!moving)
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Stage did not become idle in time.");
    }

    private bool IsDeviceMoving()
    {
        if (_channel is null)
        {
            return false;
        }

        try
        {
            var timeout = TimeSpan.FromMilliseconds(500);
            var bitsObj = Invoke(_channel, "GetUniversalStatusBits", timeout);
            if (bitsObj is null)
            {
                return false;
            }

            var bits = Convert.ToInt64(bitsObj);
            const long movingMask =
                16 |   // MovingClockwise
                32 |   // MovingCounterclockwise
                64 |   // JoggingClockwise
                128 |  // JoggingCounterclockwise
                512;   // Homing
            return (bits & movingMask) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void SafeCloseChannel()
    {
        try
        {
            if (_channel is not null)
            {
                TryInvoke(_channel, "Close");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Channel Close failed");
        }

        _channel = null;
        _channelDeviceId = null;
    }

    private void EnsureConnected()
    {
        if (!_connected || _channel is null)
        {
            throw new InvalidOperationException("Stage is not connected.");
        }
    }

    private void LogDeviceList(IReadOnlyList<object> infos)
    {
        if (infos.Count == 0)
        {
            SetStatus("XA device scan: none found");
            return;
        }

        SetStatus("XA devices: " + string.Join(" | ", infos.Select(DescribeDevice)));
    }

    private static bool IsLogicalChannel(object info)
    {
        var typeName = GetProp(info, "DeviceType")?.ToString() ?? string.Empty;
        return typeName.Contains("LogicalChannel", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeDevice(object info)
    {
        var device = GetStringProp(info, "Device") ?? "?";
        var type = GetProp(info, "DeviceType")?.ToString() ?? "?";
        var part = (GetStringProp(info, "PartNumber") ?? string.Empty).Trim();
        var parent = GetStringProp(info, "ParentDevice") ?? string.Empty;
        return string.IsNullOrWhiteSpace(parent)
            ? $"{device} {type} {part}".Trim()
            : $"{device} {type} parent={parent} {part}".Trim();
    }

    private object ScaleDistance() => GetEnumValue("Thorlabs.MotionControl.XA.ScaleType", "Distance");
    private object ScaleVelocity() => GetEnumValue("Thorlabs.MotionControl.XA.ScaleType", "Velocity");
    private object ScaleAcceleration() => GetEnumValue("Thorlabs.MotionControl.XA.ScaleType", "Acceleration");
    private object UnitMillimetres() => GetEnumValue("Thorlabs.MotionControl.XA.Unit", "Millimetres");

    private object GetTimeoutZero()
    {
        var timeoutType = RequireType("Thorlabs.MotionControl.XA.Timeout");
        return timeoutType.GetField("Zero", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }

    private object GetEnumValue(string typeName, string name)
    {
        var type = RequireType(typeName);
        return Enum.Parse(type, name);
    }

    private Type RequireType(string fullName)
    {
        var type = _xaAssembly?.GetType(fullName);
        return type ?? throw new InvalidOperationException($"XA type not found: {fullName} (dll={_loadedDllPath})");
    }

    private static object? Invoke(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToList();

        MethodInfo? method = null;
        foreach (var candidate in methods)
        {
            var parameters = candidate.GetParameters();
            var match = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (args[i] is null)
                {
                    continue;
                }

                if (!parameters[i].ParameterType.IsInstanceOfType(args[i])
                    && !IsAssignableEnum(parameters[i].ParameterType, args[i]!))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                method = candidate;
                break;
            }
        }

        method ??= methods.FirstOrDefault()
                   ?? throw new MissingMethodException(target.GetType().FullName, methodName);

        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static bool IsAssignableEnum(Type parameterType, object arg)
    {
        if (!parameterType.IsEnum)
        {
            return false;
        }

        return arg.GetType() == parameterType || arg.GetType().IsEnum;
    }

    private static void TryInvoke(object target, string methodName, params object?[] args)
    {
        try
        {
            var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            method?.Invoke(target, args);
        }
        catch
        {
            // optional
        }
    }

    private static object? GetProp(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static string? GetStringProp(object target, string name) => GetProp(target, name)?.ToString();

    private static void SetProp(object target, string name, object value)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new MissingMemberException(target.GetType().FullName, name);
        var converted = Convert.ChangeType(value, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType);
        prop.SetValue(target, converted);
    }

    private static string Flatten(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
        _activityLog.Write("thorlabs", status);
    }
}
