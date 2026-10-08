using System;

namespace ManiDispatch.Domain.Entities;

public class MatchCriteria
{
    public Guid TenantId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid ZonaId { get; set; }
    public Guid CategoriaId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
