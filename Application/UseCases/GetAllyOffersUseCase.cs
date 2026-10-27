using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.UseCases;

/// <summary>
/// Bandeja del aliado autenticado (SCRUM-1402/1404): Core entrega lo asignado y las PENDIENTE con oferta propia, y
/// Dispatch, dueño de los rechazos, quita las ofertas que el aliado rechazó.
/// </summary>
public class GetAllyOffersUseCase
{
    private readonly ICoreAssignmentClient _coreAssignmentClient;
    private readonly ISolicitudAssignmentRepository _assignmentRepository;
    private readonly IAllyRepository _allyRepository;

    public GetAllyOffersUseCase(
        ICoreAssignmentClient coreAssignmentClient,
        ISolicitudAssignmentRepository assignmentRepository,
        IAllyRepository allyRepository)
    {
        _coreAssignmentClient = coreAssignmentClient ?? throw new ArgumentNullException(nameof(coreAssignmentClient));
        _assignmentRepository = assignmentRepository ?? throw new ArgumentNullException(nameof(assignmentRepository));
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
    }

    /// <exception cref="UnauthorizedAccessException">La cuenta no tiene perfil de aliado en el tenant.</exception>
    public async Task<IReadOnlyList<AllyOffer>> ExecuteAsync(Guid tenantId, Guid usuarioId, string? correlationId)
    {
        var allyProfile = await _allyRepository.GetAllyProfileAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        var offers = await _coreAssignmentClient.GetOwnOffersAsync(tenantId, correlationId);
        if (offers.Count == 0)
        {
            return offers;
        }

        var rejected = (await _assignmentRepository.GetRejectedRequestIdsAsync(tenantId, usuarioId, allyProfile.Id)).ToHashSet();
        return offers.Where(o => o.EsMia || !rejected.Contains(o.Id)).ToList();
    }
}
