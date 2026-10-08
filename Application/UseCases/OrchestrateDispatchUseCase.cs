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
    private readonly IAllyRepository _allyRepository;
    private readonly IDispatchPublisher _dispatchPublisher;

    public OrchestrateDispatchUseCase(
        IAllyRepository allyRepository,
        IDispatchPublisher dispatchPublisher)
    {
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
        _dispatchPublisher = dispatchPublisher ?? throw new ArgumentNullException(nameof(dispatchPublisher));
    }

    public async Task<OrchestrateResponseDto> ExecuteAsync(
        OrchestrateRequestDto request,
        string correlationId,
        Guid tenantId)
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

        if (string.Equals(solicitud.Estado, "ASIGNADA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(solicitud.Estado, "CANCELADA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(solicitud.Estado, "FINALIZADA", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"La solicitud '{request.RequestId}' ya se encuentra en estado '{solicitud.Estado}' y no admite nuevo despacho.");
        }

        resolvedZonaGuid = solicitud.ZonaId;
        resolvedCategoriaGuid = solicitud.CategoriaId;

        var criteria = new MatchCriteria
        {
            TenantId = tenantId,
            RequestId = requestGuid,
            ZonaId = resolvedZonaGuid,
            CategoriaId = resolvedCategoriaGuid,
            Page = 1,
            PageSize = 50
        };

        var (allies, totalCount) = await _allyRepository.GetEligibleAlliesAsync(criteria);
        var alliesList = allies.ToList();

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
}
