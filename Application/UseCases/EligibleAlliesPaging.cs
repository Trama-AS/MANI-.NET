using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.UseCases;

internal static class EligibleAlliesPaging
{
    // Tamaño de página con el que se piden los elegibles a Core (su máximo es 100).
    public const int PageSize = 50;

    /// <summary>
    /// Todos los elegibles (SCRUM-1402 C1): Core pagina, así que se recorren todas las páginas. Core ordena por id; un
    /// aliado repetido entre páginas se cuenta una sola vez, y una página sin aliados nuevos termina el recorrido.
    /// </summary>
    public static async Task<List<Ally>> GetAllAsync(IAllyRepository repository, MatchCriteria criteria)
    {
        var alliesList = new List<Ally>();
        var vistos = new HashSet<Guid>();
        int page = 1;
        bool hayMas = true;
        while (hayMas)
        {
            var (allies, totalCount) = await repository.GetEligibleAlliesAsync(new MatchCriteria
            {
                TenantId = criteria.TenantId,
                RequestId = criteria.RequestId,
                ZonaId = criteria.ZonaId,
                CategoriaId = criteria.CategoriaId,
                Tipo = criteria.Tipo,
                Page = page++,
                PageSize = PageSize,
                CorrelationId = criteria.CorrelationId,
                ExcludeAllyIds = criteria.ExcludeAllyIds
            });
            var pagina = allies.ToList();
            var nuevos = pagina.Where(a => vistos.Add(a.Id)).ToList();
            alliesList.AddRange(nuevos);
            hayMas = nuevos.Count > 0 && pagina.Count >= PageSize && alliesList.Count < totalCount;
        }

        return alliesList;
    }
}
