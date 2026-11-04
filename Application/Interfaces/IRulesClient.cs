using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.Interfaces;

public record RankedAlly(Guid AllyId, int Posicion, decimal? Puntaje, IReadOnlyList<string> Razones);

public interface IRulesClient
{
    /// <summary>Orden de los candidatos según el criterio. Si Rules falla, lanza RulesServiceUnavailableException.</summary>
    Task<IReadOnlyList<RankedAlly>> RankAlliesAsync(
        string criterio, IReadOnlyCollection<Ally> candidatos, Guid tenantId, string role, string correlationId);
}
