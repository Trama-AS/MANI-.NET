using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Tests.Helpers;

/// <summary>
/// Doble del comando de asignación de Core. Como Core, identifica al aliado por la sesión (aquí, la función
/// <c>currentUsuario</c>), asigna una sola vez por solicitud y no revela al ganador en el conflicto.
/// </summary>
public sealed class InMemoryCoreAssignments : ICoreAssignmentClient
{
    private readonly ConcurrentDictionary<Guid, Guid> _assignments = new();
    private readonly Func<Guid> _currentUsuario;
    private readonly FakeAllyProfiles _profiles;
    private int _calls;

    public InMemoryCoreAssignments(FakeAllyProfiles profiles, Func<Guid> currentUsuario)
    {
        _profiles = profiles;
        _currentUsuario = currentUsuario;
    }

    public int Calls => _calls;
    public Exception? Failure { get; set; }
    public List<AllyOffer> Offers { get; } = new();
    public (Guid RequestId, string? IdempotencyKey, string? CorrelationId)? LastCommand { get; private set; }

    public async Task<AssignmentResult> AssignAsync(Guid requestId, Guid tenantId, string? idempotencyKey, string? correlationId)
    {
        Interlocked.Increment(ref _calls);
        LastCommand = (requestId, idempotencyKey, correlationId);
        if (Failure != null)
        {
            throw Failure;
        }

        var profile = await _profiles.GetAllyProfileAsync(_currentUsuario(), tenantId);
        if (profile == null)
        {
            return new AssignmentResult(AssignmentStatus.Forbidden, null, "Sin aliado.", "MANI-SOL-403");
        }

        if (_assignments.TryAdd(requestId, profile.Id))
        {
            return new AssignmentResult(AssignmentStatus.Assigned, profile.Id, "Asignada.");
        }

        return _assignments[requestId] == profile.Id
            ? new AssignmentResult(AssignmentStatus.AlreadyAssigned, profile.Id, "Reintento.")
            : new AssignmentResult(AssignmentStatus.Conflict, null, "Ya asignada.", "MANI-SOL-409");
    }

    public Task<IReadOnlyList<AllyOffer>> GetOwnOffersAsync(Guid tenantId, string? correlationId)
    {
        if (Failure != null)
        {
            throw Failure;
        }
        return Task.FromResult<IReadOnlyList<AllyOffer>>(Offers);
    }
}
