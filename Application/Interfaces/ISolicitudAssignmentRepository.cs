using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ManiDispatch.Application.Interfaces;

public record AllyProfile(Guid Id, Guid TenantId, Guid UsuarioId, string EstadoVerificacion);

public enum AssignmentStatus
{
    Assigned,        // Recién asignada con éxito (200)
    AlreadyAssigned, // Reintento idempotente por el mismo aliado ganador (200)
    Conflict,        // Ganada concurrentemente por otro aliado (409)
    NotFound,        // Solicitud no encontrada en el tenant (404)
    InvalidState     // Estado inválido (cancelada, finalizada, etc.) (409)
}

public record AssignmentResult(
    AssignmentStatus Status,
    Guid? AssignedAllyId,
    string? Message
);

public interface ISolicitudAssignmentRepository
{
    Task<AssignmentResult> AcceptRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId);
    Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId, string motivo);

    /// <summary>
    /// Solicitudes del tenant asignadas al aliado y no canceladas (SCRUM-853). La asignación es dominio de
    /// Dispatch (SAD §7): Core la consulta por HTTP para saber si el aliado puede leer las reglas del sitio.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid aliadoId);
}
