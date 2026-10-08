namespace ManiDispatch.Application.DTOs;

public class MatchRequestDto
{
    public string? TenantId { get; set; }
    public string? RequestId { get; set; }
    public string? ZonaId { get; set; }
    public string? CategoriaId { get; set; }
    public int? Page { get; set; } = 1;
    public int? PageSize { get; set; } = 20;

    // Backward compatibility aliases
    public string? Location
    {
        get => ZonaId;
        set => ZonaId = value;
    }

    public string? Category
    {
        get => CategoriaId;
        set => CategoriaId = value;
    }
}
