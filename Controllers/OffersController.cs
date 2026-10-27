using System;
using System.Threading.Tasks;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mani.Dispatch.Controllers;

/// <summary>
/// Bandeja del aliado autenticado (SCRUM-1402/1404): lo que tiene asignado y las ofertas PENDIENTE que recibió y no
/// rechazó. Reemplaza la lectura directa por RPC de Supabase en la app (TEC-18).
/// </summary>
[ApiController]
[Route("api/v1/dispatch")]
[Route("")]
public class OffersController : ControllerBase
{
    private readonly GetAllyOffersUseCase _getAllyOffersUseCase;

    public OffersController(GetAllyOffersUseCase getAllyOffersUseCase)
    {
        _getAllyOffersUseCase = getAllyOffersUseCase ?? throw new ArgumentNullException(nameof(getAllyOffersUseCase));
    }

    [HttpGet("offers/me")]
    public async Task<IActionResult> GetMyOffers([FromHeader(Name = "X-Correlation-ID")] string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = $"disp-{Guid.NewGuid():N}";
        }

        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            return Unauthorized(new { error = authResult.ErrorMessage, code = "MANI-DSP-401", status = StatusCodes.Status401Unauthorized, correlationId });
        }

        if (!string.Equals(authResult.Role, "aliado", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Rol no autorizado: solo un aliado consulta su bandeja de ofertas.",
                code = "MANI-DSP-403",
                status = StatusCodes.Status403Forbidden,
                correlationId
            });
        }

        if (!Guid.TryParse(authResult.UserId, out var usuarioId))
        {
            return Unauthorized(new
            {
                error = "El identificador de usuario en el token JWT no es un UUID válido.",
                code = "MANI-DSP-401",
                status = StatusCodes.Status401Unauthorized,
                correlationId
            });
        }

        try
        {
            var items = await _getAllyOffersUseCase.ExecuteAsync(authResult.TenantId!.Value, usuarioId, correlationId);
            return Ok(new { items, total = items.Count, correlationId });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message, code = "MANI-SOL-403V", status = StatusCodes.Status403Forbidden, correlationId });
        }
        catch (CoreAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message, code = ex.Code, status = StatusCodes.Status403Forbidden, correlationId });
        }
        catch (CoreServiceUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Core no está disponible para consultar la bandeja del aliado.",
                code = "CORE_SERVICE_UNAVAILABLE",
                status = StatusCodes.Status503ServiceUnavailable,
                correlationId
            });
        }
        catch (DispatchDatabaseUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No es posible consultar los rechazos del aliado por indisponibilidad de la base de datos.",
                code = "DATABASE_UNAVAILABLE",
                status = StatusCodes.Status503ServiceUnavailable,
                correlationId
            });
        }
    }
}
