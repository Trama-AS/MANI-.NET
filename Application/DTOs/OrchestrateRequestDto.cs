namespace ManiDispatch.Application.DTOs;

public class OrchestrateRequestDto
{
    public string RequestId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? ZonaId { get; set; }
    public string? CategoriaId { get; set; }

    // Compatibilidad hacia atrás con contratos anteriores
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

    public OrchestrateRequestDto() { }

    public OrchestrateRequestDto(string requestId, string? location = null, string? category = null)
    {
        RequestId = requestId;
        Location = location;
        Category = category;
    }
}
