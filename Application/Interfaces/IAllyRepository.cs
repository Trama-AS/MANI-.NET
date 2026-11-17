using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.Interfaces;

public interface IAllyRepository
{
    Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria);
    Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId);

    /// <summary>
    /// Contexto, una página de elegibles y criterio de orden de una solicitud en una sola llamada a Core (SCRUM-1652).
    /// Null si la sesión no puede ver la solicitud.
    /// </summary>
    Task<EligibleForRequest?> GetEligibleForRequestAsync(Guid requestId, Guid tenantId, string? tipo, int page, int pageSize, string correlationId);

    /// <summary>Perfil del aliado autenticado, pedido a Core con el JWT reenviado (ADR-0030). Null si no hay aliado en el tenant.</summary>
    Task<AllyProfile?> GetAllyProfileAsync(Guid usuarioId, Guid tenantId);
}
