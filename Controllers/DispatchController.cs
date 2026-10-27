using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mani.Dispatch.Controllers;

[ApiController]
[Route("api/v1/dispatch")]
[Route("")]
public class DispatchController : ControllerBase
{
    private readonly GetEligibleAlliesUseCase _getEligibleAlliesUseCase;
    private readonly OrchestrateDispatchUseCase _orchestrateDispatchUseCase;
    private readonly AcceptDispatchOfferUseCase _acceptDispatchOfferUseCase;
    private readonly RejectDispatchOfferUseCase _rejectDispatchOfferUseCase;
    private readonly ILogger<DispatchController> _logger;

    public DispatchController(
        GetEligibleAlliesUseCase getEligibleAlliesUseCase,
        OrchestrateDispatchUseCase orchestrateDispatchUseCase,
        AcceptDispatchOfferUseCase acceptDispatchOfferUseCase,
        RejectDispatchOfferUseCase rejectDispatchOfferUseCase,
        ILogger<DispatchController>? logger = null)
    {
        _getEligibleAlliesUseCase = getEligibleAlliesUseCase ?? throw new ArgumentNullException(nameof(getEligibleAlliesUseCase));
        _orchestrateDispatchUseCase = orchestrateDispatchUseCase ?? throw new ArgumentNullException(nameof(orchestrateDispatchUseCase));
        _acceptDispatchOfferUseCase = acceptDispatchOfferUseCase ?? throw new ArgumentNullException(nameof(acceptDispatchOfferUseCase));
        _rejectDispatchOfferUseCase = rejectDispatchOfferUseCase ?? throw new ArgumentNullException(nameof(rejectDispatchOfferUseCase));
        _logger = logger ?? NullLogger<DispatchController>.Instance;
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
        catch (CoreServiceUnavailableException)
        {
            return CoreUnavailable(correlationId);
        }
    }

    /// <summary>
    /// Aliados elegibles para una solicitud (RF-12, ADR-0011, SCRUM-1398/1400). Solo el cliente dueño o el admin del
    /// tenant. Zona y categoría salen siempre del contexto que entrega Core (no se aceptan por la consulta), y quedan
    /// fuera los aliados que ya rechazaron la solicitud.
    /// </summary>
    [HttpGet("requests/{requestId}/eligible-allies")]
    public async Task<IActionResult> GetEligibleAlliesForRequest(
        string requestId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? tipo = null)
    {
        var correlationId = GetOrCreateCorrelationId();

        var session = AuthenticateDispatcher(correlationId, "consultar los aliados elegibles", out var denied);
        if (denied != null)
        {
            return denied;
        }

        if (!Guid.TryParse(requestId, out var requestGuid))
        {
            return BadRequest(ErrorBody(StatusCodes.Status400BadRequest, "MANI-DSP-400",
                $"El requestId '{requestId}' no es un UUID válido.", correlationId));
        }

        try
        {
            var result = await _getEligibleAlliesUseCase.ExecuteForRequestAsync(
                requestGuid, session.TenantId, session.UsuarioId, session.Role, tipo, page, pageSize, correlationId);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ErrorBody(StatusCodes.Status404NotFound, "MANI-DSP-404",
                $"Solicitud con ID '{requestId}' no encontrada para el tenant autenticado.", correlationId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorBody(StatusCodes.Status400BadRequest, "MANI-DSP-400", ex.Message, correlationId));
        }
        catch (CoreAccessDeniedException ex)
        {
            return CoreAccessDenied(ex, correlationId);
        }
        catch (CoreServiceUnavailableException)
        {
            return CoreUnavailable(correlationId);
        }
        catch (DispatchDatabaseUnavailableException)
        {
            return DatabaseUnavailable("No es posible consultar los rechazos de la solicitud por indisponibilidad de la base de datos (ADR-0016).", correlationId);
        }
    }

    /// <summary>
    /// Orquestación de despacho de la solicitud (US-04.1.1-M6 / SCRUM-1073, SCRUM-1402).
    /// Transmite la oferta a los aliados elegibles (Broadcast conforme a ADR-0016), salvo a los que ya la rechazaron.
    /// </summary>
    [HttpPost("requests/orchestrate")]
    public async Task<IActionResult> OrchestrateDispatch([FromBody] OrchestrateRequestDto? dto)
    {
        var correlationId = GetOrCreateCorrelationId();

        // Restricción de rol (AGENTS.md y revisión de QA): solo el cliente o el admin del tenant despachan.
        var session = AuthenticateDispatcher(correlationId, "orquestar el despacho de solicitudes", out var denied);
        if (denied != null)
        {
            return denied;
        }

        if (dto != null && !string.IsNullOrWhiteSpace(dto.TenantId))
        {
            if (Guid.TryParse(dto.TenantId, out var requestedTenantGuid) && requestedTenantGuid != session.TenantId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden, "MANI-DSP-403",
                    "Aislamiento cross-tenant violado: no tiene acceso al tenant especificado (ADR-0018).", correlationId));
            }
        }

        var safeDto = dto ?? new OrchestrateRequestDto();

        try
        {
            var result = await _orchestrateDispatchUseCase.ExecuteAsync(
                safeDto, correlationId, session.TenantId, session.UsuarioId, session.Role);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ErrorBody(StatusCodes.Status404NotFound, "MANI-DSP-404", ex.Message, correlationId));
        }
        catch (DispatchDatabaseUnavailableException)
        {
            return DatabaseUnavailable("No es posible consultar los rechazos de la solicitud por indisponibilidad de la base de datos (ADR-0016).", correlationId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ErrorBody(StatusCodes.Status409Conflict, "MANI-DSP-409E", ex.Message, correlationId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorBody(StatusCodes.Status400BadRequest, "MANI-DSP-400", ex.Message, correlationId));
        }
        catch (CoreAccessDeniedException ex)
        {
            return CoreAccessDenied(ex, correlationId);
        }
        catch (CoreServiceUnavailableException)
        {
            return CoreUnavailable(correlationId);
        }
    }

    /// <summary>
    /// Aceptación de una oferta por el aliado (RF-14 / RNF-05 / SCRUM-1403). Decisión 6A: Dispatch valida y Core
    /// registra la asignación con exclusión concurrente. El reintento del mismo aliado es idempotente (200) y el
    /// perdedor recibe 409 sin saber quién ganó.
    /// </summary>
    [HttpPost("requests/{requestId}/accept")]
    public async Task<IActionResult> AcceptRequest(string requestId)
    {
        var correlationId = GetOrCreateCorrelationId();

        var session = AuthenticateAlly(correlationId, "aceptar solicitudes", out var denied);
        if (denied != null)
        {
            return denied;
        }

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            idempotencyKey = null;
        }

        try
        {
            var result = await _acceptDispatchOfferUseCase.ExecuteAsync(
                requestId, session.TenantId, session.UsuarioId, idempotencyKey, correlationId);

            _logger.LogInformation(
                "Aceptación de oferta: solicitud {RequestId}, tenant {TenantId}, usuario {UsuarioId}, resultado {Resultado}, código {Code}, correlationId {CorrelationId}, idempotencyKey {IdempotencyKey}.",
                requestId, session.TenantId, session.UsuarioId, result.Status, result.Code, correlationId, idempotencyKey);

            return result.Status switch
            {
                AssignmentStatus.Assigned => Ok(new
                {
                    message = "Solicitud asignada exitosamente al profesional.",
                    requestId,
                    assignedAllyId = result.AssignedAllyId,
                    status = "ASSIGNED",
                    isRetry = false,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                }),
                AssignmentStatus.AlreadyAssigned => Ok(new
                {
                    message = "Solicitud ya asignada previamente a este profesional (reintento idempotente).",
                    requestId,
                    assignedAllyId = result.AssignedAllyId,
                    status = "ASSIGNED",
                    isRetry = true,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                }),
                AssignmentStatus.Conflict => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-SOL-409",
                    "La solicitud ya fue aceptada por otro profesional.", correlationId)),
                AssignmentStatus.InvalidState => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-SOL-409E",
                    "La solicitud ya no está disponible para asignación.", correlationId)),
                AssignmentStatus.RejectedBefore => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-DSP-409R",
                    "Ya rechazaste esta solicitud; no puedes aceptarla.", correlationId)),
                AssignmentStatus.NotFound => NotFound(ErrorBody(StatusCodes.Status404NotFound, result.Code ?? "MANI-SOL-404",
                    $"Solicitud con ID '{requestId}' no encontrada para el tenant autenticado.", correlationId)),
                AssignmentStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden,
                    result.Code ?? "MANI-SOL-403", result.Message ?? "No puedes aceptar esta solicitud.", correlationId)),
                _ => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-SOL-409E",
                    result.Message ?? "La solicitud no está disponible para asignación.", correlationId))
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden, "MANI-SOL-403V", ex.Message, correlationId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorBody(StatusCodes.Status400BadRequest, "MANI-DSP-400", ex.Message, correlationId));
        }
        catch (CoreAccessDeniedException ex)
        {
            return CoreAccessDenied(ex, correlationId);
        }
        catch (CoreServiceUnavailableException)
        {
            // Sin respuesta de Core no se sabe si quedó asignada: el cliente reintenta con la misma Idempotency-Key.
            return CoreUnavailable(correlationId);
        }
        catch (Exception)
        {
            return DatabaseUnavailable("No es posible confirmar la aceptación de la solicitud por indisponibilidad de la base de datos (ADR-0016).", correlationId);
        }
    }

    /// <summary>
    /// Rechazo explícito de una oferta por el aliado (RF-14 / SCRUM-1404). Se registra en solicitud_rechazo: la
    /// solicitud sale de su bandeja y la siguiente orquestación no se la vuelve a ofrecer. Repetirlo es idempotente.
    /// </summary>
    [HttpPost("requests/{requestId}/reject")]
    public async Task<IActionResult> RejectRequest(string requestId, [FromBody] RejectRequestDto? dto)
    {
        var correlationId = GetOrCreateCorrelationId();

        var session = AuthenticateAlly(correlationId, "rechazar solicitudes", out var denied);
        if (denied != null)
        {
            return denied;
        }

        try
        {
            var result = await _rejectDispatchOfferUseCase.ExecuteAsync(requestId, session.TenantId, session.UsuarioId, dto?.Motivo);

            return result.Status switch
            {
                RejectionStatus.Rejected or RejectionStatus.AlreadyRejected => Ok(new
                {
                    message = "Rechazo de oferta registrado exitosamente.",
                    requestId,
                    motivo = result.Motivo,
                    status = "REJECTED",
                    isRetry = result.Status == RejectionStatus.AlreadyRejected,
                    correlationId,
                    timestamp = DateTime.UtcNow.ToString("o")
                }),
                RejectionStatus.NotFound => NotFound(ErrorBody(StatusCodes.Status404NotFound, result.Code ?? "MANI-DSP-404",
                    $"Solicitud con ID '{requestId}' no encontrada para el tenant autenticado.", correlationId)),
                RejectionStatus.AlreadyMine => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-DSP-409A",
                    "La solicitud ya está asignada a ti; no puedes rechazarla.", correlationId)),
                _ => Conflict(ErrorBody(StatusCodes.Status409Conflict, result.Code ?? "MANI-DSP-409E",
                    "La solicitud ya no está disponible.", correlationId))
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden, "MANI-SOL-403V", ex.Message, correlationId));
        }
        catch (InvalidRejectionReasonException ex)
        {
            return UnprocessableEntity(ErrorBody(StatusCodes.Status422UnprocessableEntity, InvalidRejectionReasonException.Code, ex.Message, correlationId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorBody(StatusCodes.Status400BadRequest, "MANI-DSP-400", ex.Message, correlationId));
        }
        catch (CoreAccessDeniedException ex)
        {
            return CoreAccessDenied(ex, correlationId);
        }
        catch (CoreServiceUnavailableException)
        {
            return CoreUnavailable(correlationId);
        }
        catch (Exception)
        {
            return DatabaseUnavailable("No es posible registrar el rechazo de la solicitud por indisponibilidad de la base de datos (ADR-0016).", correlationId);
        }
    }

    private readonly record struct DispatchSession(Guid TenantId, Guid UsuarioId, string Role);

    /// <summary>Sesión de aliado con usuario UUID; si no, deja en <paramref name="denied"/> la respuesta 401/403.</summary>
    private DispatchSession AuthenticateAlly(string correlationId, string accion, out IActionResult? denied)
    {
        return Authenticate(correlationId, accion, new[] { "aliado" }, "únicamente los aliados pueden", out denied);
    }

    /// <summary>Sesión de cliente o admin del tenant con usuario UUID, que son quienes despachan.</summary>
    private DispatchSession AuthenticateDispatcher(string correlationId, string accion, out IActionResult? denied)
    {
        return Authenticate(correlationId, accion, new[] { "cliente", "admin_tenant" }, "solo el cliente o el administrador del tenant pueden", out denied);
    }

    private DispatchSession Authenticate(string correlationId, string accion, string[] roles, string quien, out IActionResult? denied)
    {
        denied = null;
        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            denied = Unauthorized(ErrorBody(StatusCodes.Status401Unauthorized, "MANI-DSP-401", authResult.ErrorMessage ?? "Token inválido.", correlationId));
            return default;
        }

        var role = authResult.Role?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Array.IndexOf(roles, role) < 0)
        {
            denied = StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden, "MANI-DSP-403",
                $"Rol no autorizado: {quien} {accion}.", correlationId));
            return default;
        }

        if (!Guid.TryParse(authResult.UserId, out var usuarioId))
        {
            denied = Unauthorized(ErrorBody(StatusCodes.Status401Unauthorized, "MANI-DSP-401",
                "El identificador de usuario en el token JWT no es un UUID válido.", correlationId));
            return default;
        }

        return new DispatchSession(authResult.TenantId!.Value, usuarioId, role);
    }

    private static object ErrorBody(int status, string code, string error, string correlationId)
    {
        return new
        {
            error,
            code,
            status,
            correlationId,
            timestamp = DateTime.UtcNow.ToString("o")
        };
    }

    private ObjectResult CoreAccessDenied(CoreAccessDeniedException ex, string correlationId)
    {
        return StatusCode(StatusCodes.Status403Forbidden, ErrorBody(StatusCodes.Status403Forbidden, ex.Code, ex.Message, correlationId));
    }

    private ObjectResult DatabaseUnavailable(string error, string correlationId)
    {
        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            ErrorBody(StatusCodes.Status503ServiceUnavailable, "DATABASE_UNAVAILABLE", error, correlationId));
    }

    private ObjectResult CoreUnavailable(string correlationId)
    {
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "El servicio Core no está disponible; no se pudo completar la operación.",
            code = "CORE_SERVICE_UNAVAILABLE",
            status = StatusCodes.Status503ServiceUnavailable,
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

public record RejectRequestDto(string? Motivo);
