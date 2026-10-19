using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.Interfaces;

public interface IAllyRepository
{
    Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria);
    Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId);

    /// <summary>Perfil del aliado autenticado, pedido a Core con el JWT reenviado (ADR-0030). Null si no hay aliado en el tenant.</summary>
    Task<AllyProfile?> GetAllyProfileAsync(Guid usuarioId, Guid tenantId);
}
