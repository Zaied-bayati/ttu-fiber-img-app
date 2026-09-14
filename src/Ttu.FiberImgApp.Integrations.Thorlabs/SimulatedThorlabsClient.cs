using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Thorlabs;

/// <summary>
/// Offline / no-hardware stage that mimics NRT100 travel for UI and workflow testing.
/// </summary>
public sealed class SimulatedThorlabsClient : IThorlabsClient
{
    private readonly ThorlabsOptions _options;
    private readonly ILogger<SimulatedThorlabsClient> _logger;
    private readonly object _gate = new();
    private double _positionMm;
    private double _velocity = 5;
    private double _acceleration = 10;
    private bool _connected;
    private bool _moving;

    public SimulatedThorlabsClient(IOptions<ThorlabsOptions> options, ILogger<SimulatedThorlabsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _positionMm = _options.MinMm;
        _velocity = _options.VelocityMmPerSec;
        _acceleration = _options.AccelerationMmPerSec2;
    }

    public string Status => _connected
        ? $"Simulator connected (ch {_options.Channel}) @ {_positionMm:0.###} mm"
        : "Simulator ready — Connect to begin";

    public bool IsConnected => _connected;
    public bool IsMoving => _moving;
    public double PositionMm
    {
        get { lock (_gate) return _positionMm; }
    }

    public IReadOnlyList<string> ListDevices() => new[] { "SIM-BSC202-NRT100" };

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        _logger.LogInformation("Simulated Thorlabs stage connected");
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        _moving = false;
        return Task.CompletedTask;
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await MoveAbsoluteAsync(_options.MinMm, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAbsoluteAsync(double positionMm, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var clamped = Math.Clamp(positionMm, _options.MinMm, _options.MaxMm);
        _moving = true;
        try
        {
            double start;
            lock (_gate) start = _positionMm;
            var distance = Math.Abs(clamped - start);
            var travelMs = _velocity <= 0 ? 200 : (int)Math.Clamp((distance / _velocity) * 1000, 80, 3000);
            var steps = Math.Max(1, travelMs / 40);
            for (var i = 1; i <= steps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = (double)i / steps;
                lock (_gate) _positionMm = start + ((clamped - start) * t);
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate) _positionMm = clamped;
            _logger.LogDebug("Simulator moved to {Position} mm", clamped);
        }
        finally
        {
            _moving = false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _moving = false;
        return Task.CompletedTask;
    }

    public Task ApplyMotionParametersAsync(
        double velocityMmPerSec,
        double accelerationMmPerSec2,
        CancellationToken cancellationToken = default)
    {
        _velocity = Math.Max(0.1, velocityMmPerSec);
        _acceleration = Math.Max(0.1, accelerationMmPerSec2);
        _options.VelocityMmPerSec = _velocity;
        _options.AccelerationMmPerSec2 = _acceleration;
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Stage is not connected.");
        }
    }
}
