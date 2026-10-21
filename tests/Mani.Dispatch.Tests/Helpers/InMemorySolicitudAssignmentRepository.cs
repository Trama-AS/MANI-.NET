using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Tests.Helpers;

public class InMemorySolicitudAssignmentRepository : ISolicitudAssignmentRepository
{
    private readonly ConcurrentDictionary<Guid, Guid> _assignments = new();
    private readonly ConcurrentDictionary<(Guid SolicitudId, Guid AliadoId), string> _rejections = new();
    public Task<AssignmentResult> AcceptRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId)
    {
        if (_assignments.TryGetValue(requestId, out var currentWinner))
        {
            if (currentWinner == aliadoId)
            {
                return Task.FromResult(new AssignmentResult(
                    AssignmentStatus.AlreadyAssigned,
                    aliadoId,
                    "Solicitud ya asignada previamente a este aliado."
                ));
            }

            return Task.FromResult(new AssignmentResult(
                AssignmentStatus.Conflict,
                currentWinner,
                "La solicitud ya fue asignada a otro profesional."
            ));
        }

        if (_assignments.TryAdd(requestId, aliadoId))
        {
            return Task.FromResult(new AssignmentResult(
                AssignmentStatus.Assigned,
                aliadoId,
                "Solicitud asignada exitosamente al profesional."
            ));
        }

        _assignments.TryGetValue(requestId, out var raceWinner);
        if (raceWinner == aliadoId)
        {
            return Task.FromResult(new AssignmentResult(
                AssignmentStatus.AlreadyAssigned,
                aliadoId,
                "Solicitud ya asignada previamente a este aliado."
            ));
        }

        return Task.FromResult(new AssignmentResult(
            AssignmentStatus.Conflict,
            raceWinner,
            "La solicitud ya fue asignada a otro profesional."
        ));
    }

    public Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId, string motivo)
    {
        _rejections[(requestId, aliadoId)] = motivo;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid aliadoId)
    {
        IReadOnlyList<Guid> ids = _assignments.Where(a => a.Value == aliadoId).Select(a => a.Key).ToList();
        return Task.FromResult(ids);
    }
}
