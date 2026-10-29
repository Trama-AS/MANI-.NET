using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;

namespace ManiDispatch.Tests.Helpers;

/// <summary>
/// Doble en memoria de la base de Dispatch: rechazos por aliado y asignaciones que registró Core. Guarda el contexto
/// (usuario y rol) con el que se pidió cada operación para comprobar que se pasa el de la sesión.
/// </summary>
public class InMemorySolicitudAssignmentRepository : ISolicitudAssignmentRepository
{
    private readonly ConcurrentDictionary<Guid, Guid> _assignments = new();
    private readonly ConcurrentDictionary<(Guid SolicitudId, Guid AliadoId), (string Motivo, string? Detalle)> _rejections = new();

    public ConcurrentQueue<(Guid UsuarioId, string Role)> Contexts { get; } = new();

    /// <summary>Simula que Core asignó la solicitud al aliado.</summary>
    public void MarkAssigned(Guid requestId, Guid aliadoId) => _assignments[requestId] = aliadoId;

    public string? MotivoDe(Guid requestId, Guid aliadoId) => _rejections.TryGetValue((requestId, aliadoId), out var m) ? m.Motivo : null;

    public string? DetalleDe(Guid requestId, Guid aliadoId) => _rejections.TryGetValue((requestId, aliadoId), out var m) ? m.Detalle : null;

    public int RejectionCount => _rejections.Count;

    public Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId, string motivo, string? detalle)
    {
        Contexts.Enqueue((usuarioId, "aliado"));
        return Task.FromResult(_rejections.TryAdd((requestId, aliadoId), (motivo, detalle)));
    }

    public Task<bool> HasRejectedAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        Contexts.Enqueue((usuarioId, "aliado"));
        return Task.FromResult(_rejections.ContainsKey((requestId, aliadoId)));
    }

    public Task<IReadOnlyList<Guid>> GetRejectedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        Contexts.Enqueue((usuarioId, "aliado"));
        IReadOnlyList<Guid> ids = _rejections.Keys.Where(k => k.AliadoId == aliadoId).Select(k => k.SolicitudId).ToList();
        return Task.FromResult(ids);
    }

    public Task<IReadOnlyList<Guid>> GetRejectedAllyIdsAsync(Guid requestId, Guid tenantId, Guid usuarioId, string role)
    {
        Contexts.Enqueue((usuarioId, role));
        IReadOnlyList<Guid> ids = _rejections.Keys.Where(k => k.SolicitudId == requestId).Select(k => k.AliadoId).ToList();
        return Task.FromResult(ids);
    }

    public Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        Contexts.Enqueue((usuarioId, "aliado"));
        IReadOnlyList<Guid> ids = _assignments.Where(a => a.Value == aliadoId).Select(a => a.Key).ToList();
        return Task.FromResult(ids);
    }
}
