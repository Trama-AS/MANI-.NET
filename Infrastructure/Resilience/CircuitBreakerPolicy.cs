using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ManiDispatch.Infrastructure.Resilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// Disyuntor (Circuit Breaker, SCRUM-1663 / QAS-40) para proteger las llamadas HTTP de Dispatch hacia Core y Rules.
/// Corta llamadas de inmediato cuando el servicio aguas arriba acumula fallas pasajeras consecutivas (502/503/504,
/// errores de red o timeouts) y se recupera tras <see cref="BreakDuration"/>.
/// </summary>
public sealed class CircuitBreakerPolicy
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public CircuitBreakerPolicy(
        string name,
        int failureThreshold = 5,
        TimeSpan? breakDuration = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "El umbral de fallas debe ser al menos 1.");
        }

        Name = name ?? throw new ArgumentNullException(nameof(name));
        FailureThreshold = failureThreshold;
        BreakDuration = breakDuration ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name { get; }

    public int FailureThreshold { get; }

    public TimeSpan BreakDuration { get; }

    public CircuitState State
    {
        get
        {
            lock (_sync)
            {
                RefreshStateUnderLock();
                return _state;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    public async Task<HttpResponseMessage> ExecuteAsync(Func<Task<HttpResponseMessage>> action)
    {
        EnsurePermitted();

        HttpResponseMessage response;
        try
        {
            response = await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            RecordFailure();
            throw;
        }

        if (IsTransientStatus(response.StatusCode))
        {
            RecordFailure();
        }
        else
        {
            RecordSuccess();
        }

        return response;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _openedAt = null;
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private void EnsurePermitted()
    {
        lock (_sync)
        {
            RefreshStateUnderLock();
            if (_state == CircuitState.Open)
            {
                throw new HttpRequestException($"Circuito abierto para {Name}: el servicio aguas arriba acumula fallas consecutivas.");
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
            _state = CircuitState.Closed;
        }
    }

    private void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = _clock();
            }
        }
    }

    private void RefreshStateUnderLock()
    {
        if (_state == CircuitState.Open
            && _openedAt.HasValue
            && _clock() - _openedAt.Value >= BreakDuration)
        {
            _state = CircuitState.HalfOpen;
        }
    }
}

/// <summary>
/// DelegatingHandler que aplica <see cref="CircuitBreakerPolicy"/> en la tubería de HttpClient.
/// </summary>
public sealed class CircuitBreakerDelegatingHandler : DelegatingHandler
{
    private readonly CircuitBreakerPolicy _policy;

    public CircuitBreakerDelegatingHandler(CircuitBreakerPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _policy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
}
