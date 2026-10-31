using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.UseCases;

public class OrchestrateDispatchUseCase
{
    // Tamaño de página con el que se piden los elegibles a Core (su máximo es 100).
    private const int BroadcastPageSize = 50;

    private readonly IAllyRepository _allyRepository;
    private readonly IDispatchPublisher _dispatchPublisher;
    private readonly ISolicitudAssignmentRepository? _assignmentRepository;

    public OrchestrateDispatchUseCase(
        IAllyRepository allyRepository,
        IDispatchPublisher dispatchPublisher,
        ISolicitudAssignmentRepository? assignmentRepository = null)
    {
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
        _dispatchPublisher = dispatchPublisher ?? throw new ArgumentNullException(nameof(dispatchPublisher));
        _assignmentRepository = assignmentRepository;
    }

    /// <param name="usuarioId">Cliente o admin que despacha: con su contexto se leen los rechazos (SCRUM-1404).</param>
    /// <param name="role">Rol de la sesión (cliente o admin_tenant).</param>
    public async Task<OrchestrateResponseDto> ExecuteAsync(
        OrchestrateRequestDto request,
        string correlationId,
        Guid tenantId,
        Guid? usuarioId = null,
        string? role = null)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new ArgumentException("El identificador de solicitud (RequestId) es obligatorio para orquestar el despacho.");
        }

        if (!Guid.TryParse(request.RequestId, out var requestGuid))
        {
            throw new ArgumentException($"El RequestId '{request.RequestId}' no es un UUID válido.");
        }

        Guid resolvedZonaGuid;
        Guid resolvedCategoriaGuid;

        var solicitud = await _allyRepository.GetSolicitudContextAsync(requestGuid, tenantId);

        if (solicitud == null)
        {
            throw new KeyNotFoundException($"Solicitud con ID '{request.RequestId}' no encontrada para el tenant autenticado.");
        }

        // D-6: solo se despacha una solicitud PENDIENTE; cualquier otro estado ya salió del broadcast.
        if (!string.Equals(solicitud.Estado, "PENDIENTE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MANI-DSP-409E: la solicitud '{request.RequestId}' ya se encuentra en estado '{solicitud.Estado}' y no admite nuevo despacho.");
        }

        resolvedZonaGuid = solicitud.ZonaId;
        resolvedCategoriaGuid = solicitud.CategoriaId;

        // Idempotencia (RNF-03): verificar si ya existen ofertas previas para este requestId y tenantId
        var existingOffers = await _dispatchPublisher.GetOffersByRequestAsync(requestGuid, tenantId);
        if (existingOffers != null && existingOffers.Count > 0)
        {
            var existingDtoList = existingOffers.Select(o => new OrchestratedOfferDto
            {
                AllyId = o.AllyId.ToString(),
                NombreRazonSocial = o.AllyNombre,
                EstadoOferta = o.Estado,
                FechaOferta = o.Timestamp
            }).ToList();

            return new OrchestrateResponseDto
            {
                Message = $"Despacho ya transmitido previamente. Reintento idempotente con {existingDtoList.Count} ofertas (RNF-03).",
                RequestId = request.RequestId,
                TenantId = tenantId.ToString(),
                ZonaId = resolvedZonaGuid.ToString(),
                CategoriaId = resolvedCategoriaGuid.ToString(),
                Status = "DISPATCHED",
                CorrelationId = correlationId,
                CandidatesCount = existingDtoList.Count,
                CandidatesNotified = existingDtoList,
                Timestamp = existingOffers.First().Timestamp
            };
        }

        // Los aliados que ya rechazaron no reciben otra vez la oferta (SCRUM-1404): Dispatch se los pasa a Core.
        IReadOnlyCollection<Guid> excludeAllyIds = Array.Empty<Guid>();
        if (_assignmentRepository != null && usuarioId.HasValue && !string.IsNullOrWhiteSpace(role))
        {
            excludeAllyIds = await _assignmentRepository.GetRejectedAllyIdsAsync(requestGuid, tenantId, usuarioId.Value, role);
        }

        var alliesList = await GetAllEligibleAlliesAsync(new MatchCriteria
        {
            TenantId = tenantId,
            RequestId = requestGuid,
            ZonaId = resolvedZonaGuid,
            CategoriaId = resolvedCategoriaGuid,
            PageSize = BroadcastPageSize,
            CorrelationId = correlationId,
            ExcludeAllyIds = excludeAllyIds
        });

        var notifiedOffers = new List<OrchestratedOfferDto>();
        var now = DateTime.UtcNow;

        foreach (var ally in alliesList)
        {
            var offer = new DispatchOffer(
                RequestId: requestGuid,
                TenantId: tenantId,
                AllyId: ally.Id,
                AllyNombre: ally.NombreRazonSocial,
                Estado: "ENVIADA",
                CorrelationId: correlationId,
                Timestamp: now
            );

            await _dispatchPublisher.PublishOfferAsync(offer);

            notifiedOffers.Add(new OrchestratedOfferDto
            {
                AllyId = ally.Id.ToString(),
                NombreRazonSocial = ally.NombreRazonSocial,
                EstadoOferta = "ENVIADA",
                FechaOferta = now
            });
        }

        bool hasCandidates = notifiedOffers.Count > 0;
        string status = hasCandidates ? "DISPATCHED" : "NO_ELIGIBLE_ALLIES";
        string message = hasCandidates
            ? $"Despacho orquestado con éxito. Oferta transmitida a {notifiedOffers.Count} aliados elegibles (ADR-0016)."
            : "Despacho procesado: no se encontraron aliados con cobertura activa para la zona y categoría especificadas.";

        return new OrchestrateResponseDto
        {
            Message = message,
            RequestId = request.RequestId,
            TenantId = tenantId.ToString(),
            ZonaId = resolvedZonaGuid.ToString(),
            CategoriaId = resolvedCategoriaGuid.ToString(),
            Status = status,
            CorrelationId = correlationId,
            CandidatesCount = notifiedOffers.Count,
            CandidatesNotified = notifiedOffers,
            Timestamp = now
        };
    }

    /// <summary>
    /// El broadcast llega a todos los elegibles (SCRUM-1402 C1): Core pagina, así que se recorren todas las páginas.
    /// Core ordena por id; un aliado repetido entre páginas se cuenta una sola vez, y una página sin aliados nuevos
    /// termina el recorrido.
    /// </summary>
    private async Task<List<Ally>> GetAllEligibleAlliesAsync(MatchCriteria criteria)
    {
        var alliesList = new List<Ally>();
        var vistos = new HashSet<Guid>();
        int page = 1;
        bool hayMas = true;
        while (hayMas)
        {
            var (allies, totalCount) = await _allyRepository.GetEligibleAlliesAsync(new MatchCriteria
            {
                TenantId = criteria.TenantId,
                RequestId = criteria.RequestId,
                ZonaId = criteria.ZonaId,
                CategoriaId = criteria.CategoriaId,
                Tipo = criteria.Tipo,
                Page = page++,
                PageSize = criteria.PageSize,
                CorrelationId = criteria.CorrelationId,
                ExcludeAllyIds = criteria.ExcludeAllyIds
            });
            var pagina = allies.ToList();
            var nuevos = pagina.Where(a => vistos.Add(a.Id)).ToList();
            alliesList.AddRange(nuevos);
            hayMas = nuevos.Count > 0 && pagina.Count >= criteria.PageSize && alliesList.Count < totalCount;
        }

        return alliesList;
    }
}
