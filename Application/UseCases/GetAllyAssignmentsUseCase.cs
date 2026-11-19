using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Application.UseCases;

/// <summary>
/// Solicitudes asignadas al aliado autenticado (SCRUM-853 / SCRUM-1660, QAS-20). El aliado y sus solicitudes
/// asignadas se consultan a Core por HTTP (<see cref="ICoreAssignmentClient.GetOwnOffersAsync"/> filtrando
/// <c>EsMia == true</c>), de modo que Dispatch (<c>mani_dispatch_svc</c>) no lee directamente la tabla
/// <c>solicitud</c> del dominio de Core (ADR-0030).
/// </summary>
public class GetAllyAssignmentsUseCase
{
    private readonly ICoreAssignmentClient _coreAssignmentClient;
    private readonly IAllyRepository _allyRepository;

    public GetAllyAssignmentsUseCase(ICoreAssignmentClient coreAssignmentClient, IAllyRepository allyRepository)
    {
        _coreAssignmentClient = coreAssignmentClient ?? throw new ArgumentNullException(nameof(coreAssignmentClient));
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
    }

    /// <exception cref="UnauthorizedAccessException">La cuenta no tiene perfil de aliado en el tenant.</exception>
    public async Task<IReadOnlyList<Guid>> ExecuteAsync(Guid tenantId, Guid usuarioId, string? correlationId = null)
    {
        var allyProfile = await _allyRepository.GetAllyProfileAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        var offers = await _coreAssignmentClient.GetOwnOffersAsync(tenantId, correlationId);
        return offers.Where(o => o.EsMia).Select(o => o.Id).ToList();
    }
}
