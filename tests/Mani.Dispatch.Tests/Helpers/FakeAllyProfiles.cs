using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Tests.Helpers;

/// <summary>
/// Doble de IAllyRepository que solo sabe de perfiles de aliado y del contexto de las solicitudes (lo que Core
/// entrega a Dispatch). Una solicitud no registrada responde null, como el 404 de Core.
/// </summary>
public class FakeAllyProfiles : IAllyRepository
{
    private readonly ConcurrentDictionary<Guid, AllyProfile> _allies = new();
    private readonly ConcurrentDictionary<Guid, SolicitudContext> _solicitudes = new();

    public void RegisterAlly(AllyProfile profile) => _allies[profile.UsuarioId] = profile;

    public void RegisterSolicitud(SolicitudContext solicitud) => _solicitudes[solicitud.Id] = solicitud;

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

    public Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId)
    {
        if (_solicitudes.TryGetValue(requestId, out var solicitud) && solicitud.TenantId == tenantId)
        {
            return Task.FromResult<SolicitudContext?>(solicitud);
        }
        return Task.FromResult<SolicitudContext?>(null);
    }
}
