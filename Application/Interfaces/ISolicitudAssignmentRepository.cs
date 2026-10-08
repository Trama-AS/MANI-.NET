using System;
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
    Task<AllyProfile?> GetAllyByUserIdAsync(Guid usuarioId, Guid tenantId);
    Task<AssignmentResult> AcceptRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId);
    Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId, string motivo);
}
