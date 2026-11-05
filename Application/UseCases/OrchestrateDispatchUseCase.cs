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

        // Idempotencia (RNF-03): las ofertas ya publicadas no se repiten; un reintento completa solo las que faltan.
        var existingOffers = await _dispatchPublisher.GetOffersByRequestAsync(requestGuid, tenantId)
            ?? Array.Empty<DispatchOffer>();

        // Los aliados que ya rechazaron no reciben otra vez la oferta (SCRUM-1404): Dispatch se los pasa a Core.
        IReadOnlyCollection<Guid> excludeAllyIds = Array.Empty<Guid>();
        if (_assignmentRepository != null && usuarioId.HasValue && !string.IsNullOrWhiteSpace(role))
        {
            excludeAllyIds = await _assignmentRepository.GetRejectedAllyIdsAsync(requestGuid, tenantId, usuarioId.Value, role);
        }

        var alliesList = await EligibleAlliesPaging.GetAllAsync(_allyRepository, new MatchCriteria
        {
            TenantId = tenantId,
            RequestId = requestGuid,
            ZonaId = resolvedZonaGuid,
            CategoriaId = resolvedCategoriaGuid,
            CorrelationId = correlationId,
            ExcludeAllyIds = excludeAllyIds
        });

        var alreadyOffered = existingOffers.Select(o => o.AllyId).ToHashSet();
        var notifiedOffers = existingOffers.Select(o => new OrchestratedOfferDto
        {
            AllyId = o.AllyId.ToString(),
            NombreRazonSocial = o.AllyNombre,
            EstadoOferta = o.Estado,
            FechaOferta = o.Timestamp
        }).ToList();
        var now = DateTime.UtcNow;
        int published = 0;

        foreach (var ally in alliesList.Where(a => !alreadyOffered.Contains(a.Id)))
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
            published++;

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
        return new OrchestrateResponseDto
        {
            Message = BuildMessage(notifiedOffers.Count, existingOffers.Count, published),
            RequestId = request.RequestId,
            TenantId = tenantId.ToString(),
            ZonaId = resolvedZonaGuid.ToString(),
            CategoriaId = resolvedCategoriaGuid.ToString(),
            Status = status,
            CorrelationId = correlationId,
            CandidatesCount = notifiedOffers.Count,
            CandidatesNotified = notifiedOffers,
            Timestamp = published == 0 && existingOffers.Count > 0 ? existingOffers[0].Timestamp : now
        };
    }

    private static string BuildMessage(int total, int previous, int published)
    {
        if (total == 0)
        {
            return "Despacho procesado: no se encontraron aliados con cobertura activa para la zona y categoría especificadas.";
        }
        if (previous == 0)
        {
            return $"Despacho orquestado con éxito. Oferta transmitida a {total} aliados elegibles (ADR-0016).";
        }
        return published == 0
            ? $"Despacho ya transmitido previamente. Reintento idempotente con {total} ofertas (RNF-03)."
            : $"Reintento idempotente: se completaron {published} ofertas faltantes, {total} en total (RNF-03).";
    }
}
