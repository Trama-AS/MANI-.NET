using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Tests.Helpers;

/// <summary>Doble de IAllyRepository que solo sabe de perfiles de aliado (lo que Core entrega a Dispatch).</summary>
public class FakeAllyProfiles : IAllyRepository
{
    private readonly ConcurrentDictionary<Guid, AllyProfile> _allies = new();

    public void RegisterAlly(AllyProfile profile) => _allies[profile.UsuarioId] = profile;

    public Task<AllyProfile?> GetAllyProfileAsync(Guid usuarioId, Guid tenantId)
    {
        if (_allies.TryGetValue(usuarioId, out var profile) && profile.TenantId == tenantId)
        {
            return Task.FromResult<AllyProfile?>(profile);
        }
        return Task.FromResult<AllyProfile?>(null);
    }

    public Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria) =>
        throw new NotSupportedException();

    public Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId) =>
        throw new NotSupportedException();
}
