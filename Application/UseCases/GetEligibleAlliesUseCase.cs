using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;

namespace ManiDispatch.Application.UseCases;

public class GetEligibleAlliesUseCase
{
    // Valores canónicos de aliado.tipo: los tres tipos del SRS (RF-05), decisión de la PO en SCRUM-862
    public static readonly string[] TiposAliado = { "PERSONA_NATURAL", "PERSONA_JURIDICA", "EMPLEADO_DIRECTO" };

    private readonly IAllyRepository _allyRepository;
    private readonly ISolicitudAssignmentRepository? _assignmentRepository;
    private readonly ITenantRulesReader? _tenantRulesReader;
    private readonly IRulesClient? _rulesClient;

    public GetEligibleAlliesUseCase(
        IAllyRepository allyRepository,
        ISolicitudAssignmentRepository? assignmentRepository = null,
        ITenantRulesReader? tenantRulesReader = null,
        IRulesClient? rulesClient = null)
    {
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
        _assignmentRepository = assignmentRepository;
        _tenantRulesReader = tenantRulesReader;
        _rulesClient = rulesClient;
    }

    /// <summary>
    /// Aliados elegibles para una solicitud del cliente o del admin (SCRUM-1398/1400), en el orden que da Rules con el
    /// criterio vigente del tenant (SCRUM-1462). Zona y categoría salen siempre del contexto que entrega Core, nunca de
    /// la consulta, y se excluyen los aliados que ya la rechazaron. El orden se aplica antes de paginar.
    /// </summary>
    /// <exception cref="KeyNotFoundException">La solicitud no existe o la sesión no puede verla.</exception>
    /// <exception cref="RulesServiceUnavailableException">No se pudo ordenar; no se presenta otro orden (T2).</exception>
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

        var tipoNormalizado = NormalizeTipo(tipo);
        IReadOnlyCollection<Guid> excludeAllyIds = _assignmentRepository != null
            ? await _assignmentRepository.GetRejectedAllyIdsAsync(requestId, tenantId, usuarioId, role)
            : Array.Empty<Guid>();

        var allies = await EligibleAlliesPaging.GetAllAsync(_allyRepository, new MatchCriteria
        {
            TenantId = tenantId,
            RequestId = requestId,
            ZonaId = solicitud.ZonaId,
            CategoriaId = solicitud.CategoriaId,
            Tipo = tipoNormalizado,
            CorrelationId = correlationId,
            ExcludeAllyIds = excludeAllyIds
        });

        int resolvedPage = ResolvePage(page);
        int resolvedPageSize = ResolvePageSize(pageSize);
        var response = new MatchResponseDto
        {
            Message = "Aliados válidos obtenidos con éxito por cobertura y categoría (RF-12, ADR-0011).",
            CorrelationId = correlationId,
            TenantId = tenantId.ToString(),
            RequestId = requestId.ToString(),
            ZonaId = solicitud.ZonaId.ToString(),
            CategoriaId = solicitud.CategoriaId.ToString(),
            Tipo = tipoNormalizado,
            Page = resolvedPage,
            PageSize = resolvedPageSize,
            TotalCount = allies.Count,
            Timestamp = DateTime.UtcNow
        };
        if (allies.Count == 0)
        {
            return response;
        }

        if (_tenantRulesReader == null || _rulesClient == null)
        {
            throw new RulesServiceUnavailableException("El cliente de Rules no está configurado.");
        }

        var criterio = await _tenantRulesReader.GetCriterioOrdenListadoAsync(tenantId, correlationId);
        var ranking = await _rulesClient.RankAlliesAsync(criterio, allies, tenantId, role, correlationId);

        response.Ranking = new RankingInfoDto { Criterio = criterio };
        response.MatchedAllies = ApplyRanking(allies, ranking)
            .Skip((resolvedPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .ToList();
        return response;
    }

    // Una respuesta de Rules que no cubre exactamente a los candidatos no es un orden válido: no se pierde ni se inventa a nadie.
    private static List<AllyDto> ApplyRanking(List<Ally> allies, IReadOnlyList<RankedAlly> ranking)
    {
        var porId = allies.ToDictionary(a => a.Id);
        if (ranking.Count != allies.Count || ranking.Select(r => r.AllyId).Distinct().Count() != allies.Count
            || !ranking.All(r => porId.ContainsKey(r.AllyId)))
        {
            throw new RulesServiceUnavailableException("Rules devolvió un orden que no corresponde a los candidatos.");
        }

        return ranking.OrderBy(r => r.Posicion).Select(r =>
        {
            var dto = ToDto(porId[r.AllyId]);
            dto.Posicion = r.Posicion;
            dto.Puntaje = r.Puntaje;
            dto.Razones = r.Razones;
            return dto;
        }).ToList();
    }

    private static AllyDto ToDto(Ally a) => new()
    {
        AllyId = a.Id.ToString(),
        NombreRazonSocial = a.NombreRazonSocial,
        Tipo = a.Tipo,
        EstadoVerificacion = a.EstadoVerificacion
    };

    private static string? NormalizeTipo(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo))
        {
            return null;
        }

        var normalizado = tipo.Trim().ToUpperInvariant();
        if (Array.IndexOf(TiposAliado, normalizado) < 0)
        {
            throw new ArgumentException($"El parámetro Tipo '{tipo}' no es válido. Valores permitidos: {string.Join(", ", TiposAliado)}.", nameof(tipo));
        }
        return normalizado;
    }

    private static int ResolvePage(int? page) => page.HasValue && page.Value > 0 ? page.Value : 1;

    private static int ResolvePageSize(int? pageSize) => pageSize.HasValue && pageSize.Value > 0 ? pageSize.Value : 20;

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

        var tipo = NormalizeTipo(request.Tipo);
        int page = ResolvePage(request.Page);
        int pageSize = ResolvePageSize(request.PageSize);

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

        var allyDtos = allies.Select(ToDto).ToList();

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
