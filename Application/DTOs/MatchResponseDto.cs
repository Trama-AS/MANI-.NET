using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ManiDispatch.Application.DTOs;

public class AllyDto
{
    public string AllyId { get; set; } = string.Empty;
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string EstadoVerificacion { get; set; } = string.Empty;

    // Orden de Rules (SCRUM-1462): solo si la lista viene con ranking.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Posicion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Puntaje { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Razones { get; set; }

    // Backward compatibility property for existing consumers
    public string Name
    {
        get => NombreRazonSocial;
        set => NombreRazonSocial = value;
    }
}

public class RankingInfoDto
{
    public string Estado { get; set; } = "APLICADO";
    public string Criterio { get; set; } = string.Empty;
}

public class MatchResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string ZonaId { get; set; } = string.Empty;
    public string CategoriaId { get; set; } = string.Empty;
    public string? Tipo { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RankingInfoDto? Ranking { get; set; }
    public IEnumerable<AllyDto> MatchedAllies { get; set; } = new List<AllyDto>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
