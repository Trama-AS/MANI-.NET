namespace ManiDispatch.Domain.Entities;

public class MatchCriteria
{
    public string TenantId { get; set; } = string.Empty;
    public string ZonaId { get; set; } = string.Empty;
    public string CategoriaId { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
