using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mani.Dispatch.Controllers;

[ApiController]
[Route("api/v1/dispatch")]
[Route("")]
public class DispatchController : ControllerBase
{
    // Memoria atómica para demostrar exclusión concurrente (RNF-05)
    private static readonly ConcurrentDictionary<string, string> AcceptedRequests = new();
    private readonly GetEligibleAlliesUseCase _getEligibleAlliesUseCase;
    private readonly OrchestrateDispatchUseCase _orchestrateDispatchUseCase;

    public DispatchController(
        GetEligibleAlliesUseCase getEligibleAlliesUseCase,
        OrchestrateDispatchUseCase orchestrateDispatchUseCase)
    {
        _getEligibleAlliesUseCase = getEligibleAlliesUseCase ?? throw new ArgumentNullException(nameof(getEligibleAlliesUseCase));
        _orchestrateDispatchUseCase = orchestrateDispatchUseCase ?? throw new ArgumentNullException(nameof(orchestrateDispatchUseCase));
    }

    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            status = "UP",
            service = "MANI-Dispatch-DotNet",
            timestamp = DateTime.UtcNow.ToString("o"),
            correlationId = GetOrCreateCorrelationId()
        });
    }

    /// <summary>
    /// Algoritmo de emparejamiento por elegibilidad de categoría y zona geográfica (RF-12, ADR-0011).
    /// Requiere token JWT verificado para resolver el tenant (ADR-0018).
    /// </summary>
    [HttpPost("match")]
    public async Task<IActionResult> MatchProfessionals([FromBody] MatchRequestDto? request)
    {
        var correlationId = GetOrCreateCorrelationId();

        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            return Unauthorized(new
            {
                error = authResult.ErrorMessage,
                status = StatusCodes.Status401Unauthorized,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        var tenantId = authResult.TenantId!.Value;

        if (request != null && !string.IsNullOrWhiteSpace(request.TenantId))
        {
            if (Guid.TryParse(request.TenantId, out var requestedTenantGuid) && requestedTenantGuid != tenantId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Aislamiento cross-tenant violado: el tenant solicitado no coincide con la sesión autenticada (ADR-0018).",
                    status = StatusCodes.Status403Forbidden,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
        }

        var safeRequest = request ?? new MatchRequestDto();

        try
        {
            var result = await _getEligibleAlliesUseCase.ExecuteAsync(safeRequest, correlationId, tenantId, safeRequest.RequestId);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = ex.Message,
                status = StatusCodes.Status400BadRequest,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }

    /// <summary>
    /// Consulta de aliados válidos para una solicitud específica (RF-12, ADR-0011).
    /// Si zonaId o categoriaId no vienen en la consulta, se resuelven automáticamente de la solicitud en base de datos.
    /// </summary>
    [HttpGet("requests/{requestId}/eligible-allies")]
    public async Task<IActionResult> GetEligibleAlliesForRequest(
        string requestId,
        [FromQuery] string? zonaId,
        [FromQuery] string? categoriaId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        var correlationId = GetOrCreateCorrelationId();

        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            return Unauthorized(new
            {
                error = authResult.ErrorMessage,
                status = StatusCodes.Status401Unauthorized,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        var tenantId = authResult.TenantId!.Value;

        if (!Guid.TryParse(requestId, out var requestGuid))
        {
            return BadRequest(new
            {
                error = $"El requestId '{requestId}' no es un UUID válido.",
                status = StatusCodes.Status400BadRequest,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        string resolvedZonaId = zonaId ?? string.Empty;
        string resolvedCategoriaId = categoriaId ?? string.Empty;

        // Si zona o categoría no vienen en query params, se consulta la solicitud real del tenant
        if (string.IsNullOrWhiteSpace(resolvedZonaId) || string.IsNullOrWhiteSpace(resolvedCategoriaId))
        {
            var solicitud = await _getEligibleAlliesUseCase.GetSolicitudContextAsync(requestGuid, tenantId);
            if (solicitud == null)
            {
                return NotFound(new
                {
                    error = $"Solicitud con ID '{requestId}' no encontrada para el tenant autenticado.",
                    status = StatusCodes.Status404NotFound,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }

            resolvedZonaId = solicitud.ZonaId.ToString();
            resolvedCategoriaId = solicitud.CategoriaId.ToString();
        }

        var requestDto = new MatchRequestDto
        {
            TenantId = tenantId.ToString(),
            RequestId = requestId,
            ZonaId = resolvedZonaId,
            CategoriaId = resolvedCategoriaId,
            Page = page,
            PageSize = pageSize
        };

        try
        {
            var result = await _getEligibleAlliesUseCase.ExecuteAsync(requestDto, correlationId, tenantId, requestId);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = ex.Message,
                status = StatusCodes.Status400BadRequest,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }

    /// <summary>
    /// Orquestación de despacho de la solicitud (US-04.1.1-M6 / SCRUM-1073).
    /// Transmite la oferta a los aliados elegibles (Broadcast conforme a ADR-0016).
    /// </summary>
    [HttpPost("requests/orchestrate")]
    public async Task<IActionResult> OrchestrateDispatch([FromBody] OrchestrateRequestDto? dto)
    {
        var correlationId = GetOrCreateCorrelationId();

        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            return Unauthorized(new
            {
                error = authResult.ErrorMessage,
                status = StatusCodes.Status401Unauthorized,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        var tenantId = authResult.TenantId!.Value;

        if (dto != null && !string.IsNullOrWhiteSpace(dto.TenantId))
        {
            if (Guid.TryParse(dto.TenantId, out var requestedTenantGuid) && requestedTenantGuid != tenantId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Aislamiento cross-tenant violado: no tiene acceso al tenant especificado (ADR-0018).",
                    status = StatusCodes.Status403Forbidden,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
        }

        var safeDto = dto ?? new OrchestrateRequestDto();

        try
        {
            var result = await _orchestrateDispatchUseCase.ExecuteAsync(safeDto, correlationId, tenantId);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new
            {
                error = ex.Message,
                status = StatusCodes.Status404NotFound,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new
            {
                error = ex.Message,
                status = StatusCodes.Status409Conflict,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = ex.Message,
                status = StatusCodes.Status400BadRequest,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }

    /// <summary>
    /// Aceptación de servicio con exclusión concurrente atómica (RF-14 / RNF-05).
    /// </summary>
    [HttpPost("requests/{requestId}/accept")]
    public IActionResult AcceptRequest(string requestId, [FromBody] AcceptRequestDto dto)
    {
        var correlationId = GetOrCreateCorrelationId();

        if (dto == null || string.IsNullOrWhiteSpace(dto.AllyId))
        {
            return BadRequest(new
            {
                error = "El identificador del aliado (AllyId) es obligatorio.",
                status = StatusCodes.Status400BadRequest,
                correlationId,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        bool wonAssignment = AcceptedRequests.TryAdd(requestId, dto.AllyId);

        if (!wonAssignment)
        {
            AcceptedRequests.TryGetValue(requestId, out var winningAllyId);
            return Conflict(new
            {
                error = "La solicitud ya fue aceptada por otro profesional.",
                status = StatusCodes.Status409Conflict,
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

    private string GetOrCreateCorrelationId()
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = $"disp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }
        return correlationId;
    }
}

public record AcceptRequestDto(string AllyId);
