namespace ManiDispatch.Application.DTOs;

public class MatchRequestDto
{
    public string? TenantId { get; set; }
    public string? ZonaId { get; set; }
    public string? Location { get; set; }
    public string? CategoriaId { get; set; }
    public string? Category { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
