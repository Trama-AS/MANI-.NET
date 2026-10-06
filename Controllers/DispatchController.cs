using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace Mani.Dispatch.Controllers;

[ApiController]
[Route("api/v1/dispatch")]
public class DispatchController : ControllerBase
{
    // Memoria atómica para demostrar exclusión concurrente (RNF-05)
    private static readonly ConcurrentDictionary<string, string> AcceptedRequests = new();

    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            status = "UP",
            service = "MANI-Dispatch-DotNet",
            timestamp = DateTime.UtcNow.ToString("o"),
            correlationId = Request.Headers["X-Correlation-ID"].ToString()
        });
    }

    /// <summary>
    /// Algoritmo de emparejamiento por cercanía geográfica (RF-12).
    /// </summary>
    [HttpPost("match")]
    public IActionResult MatchProfessionals([FromBody] MatchRequestDto? request)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();

        // Lista simulada de profesionales emparejados por cercanía
        var matched = new[]
        {
            new { AllyId = "aliado-1", Name = "Carolina Gómez", DistanceKm = 1.2, EstimatedArrivalMinutes = 15 },
            new { AllyId = "aliado-2", Name = "Paola Morales", DistanceKm = 2.5, EstimatedArrivalMinutes = 25 },
            new { AllyId = "aliado-3", Name = "Sandra Rivas", DistanceKm = 4.1, EstimatedArrivalMinutes = 40 }
        };

        return Ok(new
        {
            service = "MANI-Dispatch-DotNet",
            correlationId,
            clientLocation = request?.Location ?? "Bogotá, Chapinero",
            category = request?.Category ?? "Manicura Tradicional",
            candidates = matched
        });
    }

    /// <summary>
    /// Aceptación de servicio con exclusión concurrente atómica (RF-14 / RNF-05).
    /// Si dos aliados aceptan al mismo tiempo, el primero gana (200 OK) y los demás reciben 409 Conflict.
    /// </summary>
    [HttpPost("requests/{requestId}/accept")]
    public IActionResult AcceptRequest(string requestId, [FromBody] AcceptRequestDto dto)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();

        // Intento de inserción atómica
        bool wonAssignment = AcceptedRequests.TryAdd(requestId, dto.AllyId);

        if (!wonAssignment)
        {
            // Conflicto de exclusión concurrente: Ya fue asignada a otro profesional
            AcceptedRequests.TryGetValue(requestId, out var winningAllyId);
            return Conflict(new
            {
                error = "La solicitud ya fue aceptada por otro profesional.",
                status = 409,
                correlationId,
                assignedTo = winningAllyId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        return Ok(new
        {
            message = "Solicitud asignada exitosamente al profesional.",
            requestId,
            assignedAllyId = dto.AllyId,
            status = "ASSIGNED",
            correlationId,
            timestamp = DateTime.UtcNow.ToString("o")
        });
    }
}

public record MatchRequestDto(string? Location, string? Category);
public record AcceptRequestDto(string AllyId);
