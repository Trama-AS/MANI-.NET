using System;
using System.Collections.Generic;

namespace ManiDispatch.Domain.Entities;

public class MatchCriteria
{
    public Guid TenantId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid ZonaId { get; set; }
    public Guid CategoriaId { get; set; }
    public string? Tipo { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? CorrelationId { get; set; }

    /// <summary>Aliados que rechazaron la solicitud: Core los quita antes de paginar (T1, SCRUM-1398).</summary>
    public IReadOnlyCollection<Guid> ExcludeAllyIds { get; set; } = Array.Empty<Guid>();
}
