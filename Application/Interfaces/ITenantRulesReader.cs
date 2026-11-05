using System;
using System.Threading.Tasks;

namespace ManiDispatch.Application.Interfaces;

public interface ITenantRulesReader
{
    /// <summary>Criterio vigente del tenant para ordenar aliados (SCRUM-1366). Si no se puede leer, lanza RulesServiceUnavailableException.</summary>
    Task<string> GetCriterioOrdenListadoAsync(Guid tenantId, string correlationId);
}
