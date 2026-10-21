using System;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mani.Dispatch.Controllers;

/// <summary>
/// Asignaciones del aliado autenticado (SCRUM-853). Core la consulta por HTTP para dejar que el aliado asignado
/// lea las reglas del sitio de su servicio (criterio C1, T1): los estados de asignación son dominio de Dispatch.
/// </summary>
[ApiController]
[Route("api/v1/dispatch")]
[Route("")]
public class AssignmentsController : ControllerBase
{
    private readonly GetAllyAssignmentsUseCase _getAllyAssignmentsUseCase;

    public AssignmentsController(GetAllyAssignmentsUseCase getAllyAssignmentsUseCase)
    {
        _getAllyAssignmentsUseCase = getAllyAssignmentsUseCase ?? throw new ArgumentNullException(nameof(getAllyAssignmentsUseCase));
    }

    [HttpGet("assignments/me")]
    public async Task<IActionResult> GetMyAssignments()
    {
        var correlationId = Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = $"disp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        var authResult = JwtAuthHelper.Authenticate(Request.Headers.Authorization);
        if (!authResult.IsAuthenticated)
        {
            return Unauthorized(new { error = authResult.ErrorMessage, status = StatusCodes.Status401Unauthorized, correlationId });
        }

        if (!string.Equals(authResult.Role, "aliado", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Rol no autorizado: solo un aliado consulta sus asignaciones.",
                status = StatusCodes.Status403Forbidden,
                correlationId
            });
        }

        if (!Guid.TryParse(authResult.UserId, out var usuarioId))
        {
            return Unauthorized(new
            {
                error = "El identificador de usuario en el token JWT no es un UUID válido.",
                status = StatusCodes.Status401Unauthorized,
                correlationId
            });
        }

        try
        {
            var requestIds = await _getAllyAssignmentsUseCase.ExecuteAsync(authResult.TenantId!.Value, usuarioId);
            return Ok(new
            {
                requestIds = requestIds.Select(id => id.ToString()).ToList(),
                total = requestIds.Count,
                correlationId
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message, status = StatusCodes.Status403Forbidden, correlationId });
        }
        catch (CoreServiceUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Core no está disponible para identificar al aliado.",
                code = "CORE_SERVICE_UNAVAILABLE",
                status = StatusCodes.Status503ServiceUnavailable,
                correlationId
            });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "No es posible consultar las asignaciones por indisponibilidad de la base de datos.",
                code = "DATABASE_UNAVAILABLE",
                status = StatusCodes.Status503ServiceUnavailable,
                correlationId
            });
        }
    }
}
