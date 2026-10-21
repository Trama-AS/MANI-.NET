using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Application.UseCases;

/// <summary>
/// Solicitudes asignadas al aliado autenticado (SCRUM-853, criterio T1). El aliado se resuelve con Core
/// por el JWT reenviado, igual que en la aceptación; la asignación se lee del dominio de Dispatch.
/// </summary>
public class GetAllyAssignmentsUseCase
{
    private readonly ISolicitudAssignmentRepository _assignmentRepository;
    private readonly IAllyRepository _allyRepository;

    public GetAllyAssignmentsUseCase(ISolicitudAssignmentRepository assignmentRepository, IAllyRepository allyRepository)
    {
        _assignmentRepository = assignmentRepository ?? throw new ArgumentNullException(nameof(assignmentRepository));
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
    }

    /// <exception cref="UnauthorizedAccessException">La cuenta no tiene perfil de aliado en el tenant.</exception>
    public async Task<IReadOnlyList<Guid>> ExecuteAsync(Guid tenantId, Guid usuarioId)
    {
        var allyProfile = await _allyRepository.GetAllyProfileAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        return await _assignmentRepository.GetAssignedRequestIdsAsync(tenantId, allyProfile.Id);
    }
}
