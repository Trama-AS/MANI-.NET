using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.Interfaces;

public interface IAllyRepository
{
    Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria);
    Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId);
}
