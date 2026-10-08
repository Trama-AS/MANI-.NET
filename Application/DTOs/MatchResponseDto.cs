using System;
using System.Collections.Generic;

namespace ManiDispatch.Application.DTOs;

public class AllyDto
{
    public string AllyId { get; set; } = string.Empty;
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string EstadoVerificacion { get; set; } = string.Empty;

    // Backward compatibility property for existing consumers
    public string Name
    {
        get => NombreRazonSocial;
        set => NombreRazonSocial = value;
    }
}

public class MatchResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string ZonaId { get; set; } = string.Empty;
    public string CategoriaId { get; set; } = string.Empty;
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public IEnumerable<AllyDto> MatchedAllies { get; set; } = new List<AllyDto>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
