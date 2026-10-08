using System;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Application.UseCases;

public class RejectDispatchOfferUseCase
{
    private readonly ISolicitudAssignmentRepository _assignmentRepository;

    public RejectDispatchOfferUseCase(ISolicitudAssignmentRepository assignmentRepository)
    {
        _assignmentRepository = assignmentRepository ?? throw new ArgumentNullException(nameof(assignmentRepository));
    }

    public async Task<bool> ExecuteAsync(
        string requestId,
        Guid tenantId,
        Guid usuarioId,
        string? motivo = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("El identificador de solicitud (RequestId) es obligatorio.", nameof(requestId));
        }

        if (!Guid.TryParse(requestId, out var requestGuid))
        {
            throw new ArgumentException($"El RequestId '{requestId}' no es un UUID válido.", nameof(requestId));
        }

        var allyProfile = await _assignmentRepository.GetAllyByUserIdAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        bool isVerified = string.Equals(allyProfile.EstadoVerificacion, "VERIFICADO", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(allyProfile.EstadoVerificacion, "aprobado", StringComparison.OrdinalIgnoreCase);

        if (!isVerified)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: tu registro como aliado aún no está verificado.");
        }

        string safeMotivo = string.IsNullOrWhiteSpace(motivo) ? "NO_DISPONIBLE" : motivo.Trim();

        return await _assignmentRepository.RejectRequestAsync(requestGuid, tenantId, allyProfile.Id, safeMotivo);
    }
}
