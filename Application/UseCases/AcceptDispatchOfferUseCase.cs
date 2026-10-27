using System;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Application.UseCases;

/// <summary>
/// Aceptación de una oferta (SCRUM-1403). Decisión 6A: Dispatch valida al aliado y que no haya rechazado la oferta,
/// y le pide a Core, dueño del estado de la solicitud, que registre la asignación de forma atómica e idempotente.
/// </summary>
public class AcceptDispatchOfferUseCase
{
    private readonly ISolicitudAssignmentRepository _assignmentRepository;
    private readonly IAllyRepository _allyRepository;
    private readonly ICoreAssignmentClient _coreAssignmentClient;

    public AcceptDispatchOfferUseCase(
        ISolicitudAssignmentRepository assignmentRepository,
        IAllyRepository allyRepository,
        ICoreAssignmentClient coreAssignmentClient)
    {
        _assignmentRepository = assignmentRepository ?? throw new ArgumentNullException(nameof(assignmentRepository));
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
        _coreAssignmentClient = coreAssignmentClient ?? throw new ArgumentNullException(nameof(coreAssignmentClient));
    }

    public async Task<AssignmentResult> ExecuteAsync(
        string requestId,
        Guid tenantId,
        Guid usuarioId,
        string? idempotencyKey = null,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("El identificador de solicitud (RequestId) es obligatorio.", nameof(requestId));
        }

        if (!Guid.TryParse(requestId, out var requestGuid))
        {
            throw new ArgumentException($"El RequestId '{requestId}' no es un UUID válido.", nameof(requestId));
        }

        // 1. Obtener perfil del aliado y validar estado VERIFICADO (DoD §9.3 / RPC 005)
        var allyProfile = await _allyRepository.GetAllyProfileAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        bool isVerified = string.Equals(allyProfile.EstadoVerificacion, "VERIFICADO", StringComparison.OrdinalIgnoreCase);

        if (!isVerified)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: tu registro como aliado aún no está verificado.");
        }

        // 2. Quien ya rechazó la oferta no puede aceptarla después (N-3, SCRUM-1404).
        if (await _assignmentRepository.HasRejectedAsync(requestGuid, tenantId, usuarioId, allyProfile.Id))
        {
            return new AssignmentResult(AssignmentStatus.RejectedBefore, null, "Ya rechazaste esta solicitud.", "MANI-DSP-409R");
        }

        // 3. Core registra la asignación con exclusión concurrente (bloqueo de fila y UPDATE condicional).
        return await _coreAssignmentClient.AssignAsync(requestGuid, tenantId, idempotencyKey, correlationId);
    }
}
