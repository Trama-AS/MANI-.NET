using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.Interfaces;

/// <summary>
/// Comandos y consultas de asignación que Dispatch le pide a Core (decisión 6A, SCRUM-1403): Dispatch decide quién
/// acepta y Core, dueño del estado de la solicitud, lo registra. Usa el JWT del aliado reenviado (ADR-0030).
/// </summary>
public interface ICoreAssignmentClient
{
    /// <summary>
    /// Pide a Core que asigne la solicitud al aliado de la sesión. Core la registra de forma atómica e idempotente:
    /// el reintento del mismo aliado devuelve <see cref="AssignmentStatus.AlreadyAssigned"/>.
    /// </summary>
    Task<AssignmentResult> AssignAsync(Guid requestId, Guid tenantId, string? idempotencyKey, string? correlationId);

    /// <summary>Bandeja del aliado en Core: sus solicitudes asignadas y las PENDIENTE con oferta propia.</summary>
    Task<IReadOnlyList<AllyOffer>> GetOwnOffersAsync(Guid tenantId, string? correlationId);
}
