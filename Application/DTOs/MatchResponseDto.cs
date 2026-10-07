using System;
using System.Collections.Generic;

namespace ManiDispatch.Application.DTOs;

public class AllyDto
{
    public string AllyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public decimal? Rating { get; set; }
    public decimal? DistanceKm { get; set; }
    public int? EstimatedArrivalMinutes { get; set; }
}

public class MatchResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ZonaId { get; set; } = string.Empty;
    public string CategoriaId { get; set; } = string.Empty;
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public IEnumerable<AllyDto> MatchedAllies { get; set; } = new List<AllyDto>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
