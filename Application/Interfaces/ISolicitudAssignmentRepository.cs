using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ManiDispatch.Application.Interfaces;

public record AllyProfile(Guid Id, Guid TenantId, Guid UsuarioId, string EstadoVerificacion);

public enum AssignmentStatus
{
    Assigned,        // Core registró la asignación (200)
    AlreadyAssigned, // Reintento idempotente del mismo aliado (200)
    Conflict,        // Otro aliado la ganó (409 MANI-SOL-409)
    NotFound,        // Solicitud no encontrada en el tenant (404)
    InvalidState,    // Ya no está PENDIENTE: cancelada, finalizada, etc. (409 MANI-SOL-409E)
    Forbidden,       // Core negó la asignación: sin oferta propia, no elegible o aliado inactivo (403 con el código de Core)
    RejectedBefore   // El aliado ya había rechazado la oferta (409 MANI-DSP-409R)
}

/// <summary>Resultado de aceptar una oferta. <c>Code</c> lleva el código MANI-* que se devuelve al cliente.</summary>
public record AssignmentResult(
    AssignmentStatus Status,
    Guid? AssignedAllyId,
    string? Message,
    string? Code = null
);

public enum RejectionStatus
{
    Rejected,        // Rechazo registrado (200)
    AlreadyRejected, // Reintento idempotente (200)
    NotFound,        // La solicitud no existe o el aliado no la puede ver (404 MANI-DSP-404)
    AlreadyMine,     // La solicitud ya es del aliado: no puede rechazarla (409 MANI-DSP-409A)
    InvalidState     // La solicitud ya no está PENDIENTE (409 MANI-DSP-409E)
}

public record RejectionResult(RejectionStatus Status, string? Motivo, string? Code = null);

/// <summary>
/// Rechazos y consultas de asignación en la base de Dispatch (decisión 6A, SCRUM-1403). Dispatch ya no escribe
/// <c>solicitud</c>: la asignación la registra Core. Cada operación corre con el contexto de la sesión
/// (<c>app.set_context</c>) para que RLS aplique al rol <c>mani_dispatch_svc</c> (D-2).
/// </summary>
public interface ISolicitudAssignmentRepository
{
    /// <summary>Registra el rechazo del aliado. Devuelve false si ya existía (reintento idempotente).</summary>
    Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId, string? motivo);

    /// <summary>Indica si el aliado ya rechazó la solicitud (N-3: quien rechazó no puede aceptar después).</summary>
    Task<bool> HasRejectedAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId);

    /// <summary>Solicitudes que rechazó el aliado, para quitarlas de su bandeja (SCRUM-1404).</summary>
    Task<IReadOnlyList<Guid>> GetRejectedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId);

    /// <summary>
    /// Aliados que rechazaron la solicitud, leídos con el contexto del cliente o del admin que la despacha. Se envían
    /// a Core en <c>excludeAllyIds</c>: Core no lee <c>solicitud_rechazo</c> (T1).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRejectedAllyIdsAsync(Guid requestId, Guid tenantId, Guid usuarioId, string role);

    /// <summary>
    /// Solicitudes del tenant asignadas al aliado y no canceladas (SCRUM-853). Core la consulta por HTTP para saber
    /// si el aliado puede leer las reglas del sitio.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId);
}
