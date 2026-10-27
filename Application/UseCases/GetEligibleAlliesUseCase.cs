using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.UseCases;

public class GetEligibleAlliesUseCase
{
    // Valores canónicos de aliado.tipo: los tres tipos del SRS (RF-05), decisión de la PO en SCRUM-862
    public static readonly string[] TiposAliado = { "PERSONA_NATURAL", "PERSONA_JURIDICA", "EMPLEADO_DIRECTO" };

    private readonly IAllyRepository _allyRepository;
    private readonly ISolicitudAssignmentRepository? _assignmentRepository;

    public GetEligibleAlliesUseCase(IAllyRepository allyRepository, ISolicitudAssignmentRepository? assignmentRepository = null)
    {
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
        _assignmentRepository = assignmentRepository;
    }

    /// <summary>
    /// Aliados elegibles para una solicitud del cliente o del admin (SCRUM-1398/1400). Zona y categoría salen siempre
    /// del contexto que entrega Core, nunca de la consulta, y se excluyen los aliados que ya la rechazaron.
    /// </summary>
    /// <exception cref="KeyNotFoundException">La solicitud no existe o la sesión no puede verla.</exception>
    public async Task<MatchResponseDto> ExecuteForRequestAsync(
        Guid requestId,
        Guid tenantId,
        Guid usuarioId,
        string role,
        string? tipo,
        int? page,
        int? pageSize,
        string correlationId)
    {
        var solicitud = await _allyRepository.GetSolicitudContextAsync(requestId, tenantId);
        if (solicitud == null)
        {
            throw new KeyNotFoundException($"Solicitud con ID '{requestId}' no encontrada para el tenant autenticado.");
        }

        IReadOnlyCollection<Guid> excludeAllyIds = _assignmentRepository != null
            ? await _assignmentRepository.GetRejectedAllyIdsAsync(requestId, tenantId, usuarioId, role)
            : Array.Empty<Guid>();

        var request = new MatchRequestDto
        {
            TenantId = tenantId.ToString(),
            RequestId = requestId.ToString(),
            ZonaId = solicitud.ZonaId.ToString(),
            CategoriaId = solicitud.CategoriaId.ToString(),
            Tipo = tipo,
            Page = page,
            PageSize = pageSize
        };

        return await ExecuteAsync(request, correlationId, tenantId, requestId.ToString(), excludeAllyIds);
    }

    public async Task<MatchResponseDto> ExecuteAsync(
        MatchRequestDto request,
        string correlationId,
        Guid tenantId,
        string? requestId = null,
        IReadOnlyCollection<Guid>? excludeAllyIds = null)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ZonaId))
        {
            throw new ArgumentException("El parámetro ZonaId es obligatorio para calcular cobertura (ADR-0011).", nameof(request.ZonaId));
        }

        if (!Guid.TryParse(request.ZonaId, out var zonaGuid))
        {
            throw new ArgumentException($"El parámetro ZonaId '{request.ZonaId}' no es un UUID válido.", nameof(request.ZonaId));
        }

        if (string.IsNullOrWhiteSpace(request.CategoriaId))
        {
            throw new ArgumentException("El parámetro CategoriaId es obligatorio para filtrar por especialidad.", nameof(request.CategoriaId));
        }

        if (!Guid.TryParse(request.CategoriaId, out var categoriaGuid))
        {
            throw new ArgumentException($"El parámetro CategoriaId '{request.CategoriaId}' no es un UUID válido.", nameof(request.CategoriaId));
        }

        string? tipo = null;
        if (!string.IsNullOrWhiteSpace(request.Tipo))
        {
            tipo = request.Tipo.Trim().ToUpperInvariant();
            if (Array.IndexOf(TiposAliado, tipo) < 0)
            {
                throw new ArgumentException($"El parámetro Tipo '{request.Tipo}' no es válido. Valores permitidos: {string.Join(", ", TiposAliado)}.", nameof(request.Tipo));
            }
        }

        int page = request.Page.HasValue && request.Page.Value > 0 ? request.Page.Value : 1;
        int pageSize = request.PageSize.HasValue && request.PageSize.Value > 0 ? request.PageSize.Value : 20;

        Guid? parsedRequestId = null;
        if (!string.IsNullOrWhiteSpace(requestId) && Guid.TryParse(requestId, out var reqGuid))
        {
            parsedRequestId = reqGuid;
        }

        var criteria = new MatchCriteria
        {
            TenantId = tenantId,
            RequestId = parsedRequestId,
            ZonaId = zonaGuid,
            CategoriaId = categoriaGuid,
            Tipo = tipo,
            Page = page,
            PageSize = pageSize,
            CorrelationId = correlationId,
            ExcludeAllyIds = excludeAllyIds ?? Array.Empty<Guid>()
        };

        var (allies, totalCount) = await _allyRepository.GetEligibleAlliesAsync(criteria);

        var allyDtos = allies.Select(a => new AllyDto
        {
            AllyId = a.Id.ToString(),
            NombreRazonSocial = a.NombreRazonSocial,
            Tipo = a.Tipo,
            EstadoVerificacion = a.EstadoVerificacion
        }).ToList();

        return new MatchResponseDto
        {
            Message = "Aliados válidos obtenidos con éxito por cobertura y categoría (RF-12, ADR-0011).",
            CorrelationId = correlationId,
            TenantId = tenantId.ToString(),
            RequestId = requestId,
            ZonaId = request.ZonaId,
            CategoriaId = request.CategoriaId,
            Tipo = tipo,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            MatchedAllies = allyDtos,
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId)
    {
        return await _allyRepository.GetSolicitudContextAsync(requestId, tenantId);
    }
}
