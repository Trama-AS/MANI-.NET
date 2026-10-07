using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.UseCases;

namespace Mani.Dispatch.Controllers;

[ApiController]
[Route("api/v1/dispatch")]
public class DispatchController : ControllerBase
{
    // Memoria atómica para demostrar exclusión concurrente (RNF-05)
    private static readonly ConcurrentDictionary<string, string> AcceptedRequests = new();
    private readonly GetEligibleAlliesUseCase _getEligibleAlliesUseCase;

    public DispatchController(GetEligibleAlliesUseCase getEligibleAlliesUseCase)
    {
        _getEligibleAlliesUseCase = getEligibleAlliesUseCase;
    }

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
    /// Algoritmo de emparejamiento por cercanía geográfica y elegibilidad por categoría y zona (RF-12, ADR-0011).
    /// Arquitectura Limpia: delega en GetEligibleAlliesUseCase.
    /// </summary>
    [HttpPost("match")]
    public async Task<IActionResult> MatchProfessionals([FromBody] MatchRequestDto? request)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = $"disp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        var tenantId = Request.Headers["X-Tenant-Id"].ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = Request.Headers["X-Tenant-Slug"].ToString();
        }
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = request?.TenantId ?? "default-tenant";
        }

        var safeRequest = request ?? new MatchRequestDto();
        if (string.IsNullOrEmpty(safeRequest.TenantId))
        {
            safeRequest.TenantId = tenantId;
        }

        var result = await _getEligibleAlliesUseCase.ExecuteAsync(safeRequest, correlationId, tenantId);
        return Ok(result);
    }

    /// <summary>
    /// Consulta de aliados válidos para una solicitud específica (RF-12, DD_V2 / SDD_V1).
    /// </summary>
    [HttpGet("requests/{requestId}/eligible-allies")]
    public async Task<IActionResult> GetEligibleAlliesForRequest(
        string requestId, 
        [FromQuery] string? zonaId, 
        [FromQuery] string? categoriaId, 
        [FromQuery] int? page, 
        [FromQuery] int? pageSize)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = $"disp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        var tenantId = Request.Headers["X-Tenant-Id"].ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = Request.Headers["X-Tenant-Slug"].ToString();
        }
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = "default-tenant";
        }

        var requestDto = new MatchRequestDto
        {
            TenantId = tenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Page = page,
            PageSize = pageSize
        };

        var result = await _getEligibleAlliesUseCase.ExecuteAsync(requestDto, correlationId, tenantId);
        return Ok(result);
    }

    /// <summary>
    /// Aceptación de servicio con exclusión concurrente atómica (RF-14 / RNF-05).
    /// </summary>
    [HttpPost("requests/{requestId}/accept")]
    public IActionResult AcceptRequest(string requestId, [FromBody] AcceptRequestDto dto)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();

        bool wonAssignment = AcceptedRequests.TryAdd(requestId, dto.AllyId);

        if (!wonAssignment)
        {
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

public record AcceptRequestDto(string AllyId);
