using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Infrastructure.Resilience;
using Xunit;

namespace ManiDispatch.Tests;

public class CircuitBreakerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _responder(request);
        }
    }

    [Fact]
    public async Task OpensAfterReachingFailureThreshold_RejectsImmediatelyWhileOpen_AndRecoversAfterBreakDuration()
    {
        var now = DateTimeOffset.Parse("2026-11-16T20:00:00Z");
        var statusToReturn = HttpStatusCode.ServiceUnavailable;
        var stub = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(statusToReturn)));
        var policy = new CircuitBreakerPolicy(
            "core-test",
            failureThreshold: 3,
            breakDuration: TimeSpan.FromSeconds(10),
            clock: () => now);
        var handler = new CircuitBreakerDelegatingHandler(policy) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        Assert.Equal(CircuitState.Closed, policy.State);

        for (var i = 0; i < 3; i++)
        {
            var resp = await client.GetAsync("http://core.test/api");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        }

        Assert.Equal(CircuitState.Open, policy.State);
        Assert.Equal(3, policy.ConsecutiveFailures);
        Assert.Equal(3, stub.Calls);

        // Con el circuito abierto falla de inmediato sin invocar el handler subyacente
        var openEx = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://core.test/api"));
        Assert.Contains("Circuito abierto", openEx.Message, StringComparison.Ordinal);
        Assert.Equal(3, stub.Calls);

        // Avanza el reloj -> HalfOpen; si vuelve a fallar regresa a Open
        now = now.AddSeconds(10);
        Assert.Equal(CircuitState.HalfOpen, policy.State);
        await client.GetAsync("http://core.test/api");
        Assert.Equal(CircuitState.Open, policy.State);

        // Avanza de nuevo -> HalfOpen; ahora responde 200 OK -> regresa a Closed
        now = now.AddSeconds(10);
        statusToReturn = HttpStatusCode.OK;
        var recovered = await client.GetAsync("http://core.test/api");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(CircuitState.Closed, policy.State);
        Assert.Equal(0, policy.ConsecutiveFailures);
    }

    [Fact]
    public async Task NetworkAndTimeoutExceptionsCountTowardsCircuitBreakerAndResetClearsState()
    {
        var policy = new CircuitBreakerPolicy("rules-test", failureThreshold: 2, breakDuration: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            policy.ExecuteAsync(() => throw new HttpRequestException("red caída")));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            policy.ExecuteAsync(() => throw new TaskCanceledException("timeout")));

        Assert.Equal(CircuitState.Open, policy.State);

        policy.Reset();
        Assert.Equal(CircuitState.Closed, policy.State);
        Assert.Equal(0, policy.ConsecutiveFailures);
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerPolicy("x", failureThreshold: 0));
        Assert.Throws<ArgumentNullException>(() => new CircuitBreakerPolicy(null!));
        Assert.Throws<ArgumentNullException>(() => new CircuitBreakerDelegatingHandler(null!));
    }
}
