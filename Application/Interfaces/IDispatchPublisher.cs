using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ManiDispatch.Application.Interfaces;

public record DispatchOffer(
    Guid RequestId,
    Guid TenantId,
    Guid AllyId,
    string AllyNombre,
    string Estado,
    string CorrelationId,
    DateTime Timestamp
);

public interface IDispatchPublisher
{
    Task PublishOfferAsync(DispatchOffer offer);
    Task<IReadOnlyList<DispatchOffer>> GetOffersByRequestAsync(Guid requestId, Guid tenantId);
}
