using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Dispatch;

public class InMemoryDispatchPublisher : IDispatchPublisher
{
    private static readonly ConcurrentBag<DispatchOffer> PublishedOffers = new();
    private readonly ILogger<InMemoryDispatchPublisher> _logger;

    public InMemoryDispatchPublisher(ILogger<InMemoryDispatchPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishOfferAsync(DispatchOffer offer)
    {
        PublishedOffers.Add(offer);
        _logger.LogInformation(
            "[DISPATCH-BROADCAST] Oferta transmitida al aliado {AllyId} ({AllyNombre}) para solicitud {RequestId} [Tenant {TenantId}, Corr {CorrelationId}].",
            offer.AllyId, offer.AllyNombre, offer.RequestId, offer.TenantId, offer.CorrelationId);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DispatchOffer>> GetOffersByRequestAsync(Guid requestId, Guid tenantId)
    {
        var offers = PublishedOffers
            .Where(o => o.RequestId == requestId && o.TenantId == tenantId)
            .ToList();

        return Task.FromResult<IReadOnlyList<DispatchOffer>>(offers);
    }
}
