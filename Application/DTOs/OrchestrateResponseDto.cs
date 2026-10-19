using System;
using System.Collections.Generic;

namespace ManiDispatch.Application.DTOs;

public class OrchestratedOfferDto
{
    public string AllyId { get; set; } = string.Empty;
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string EstadoOferta { get; set; } = "ENVIADA";
    public DateTime FechaOferta { get; set; } = DateTime.UtcNow;
}

public class OrchestrateResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ZonaId { get; set; } = string.Empty;
    public string CategoriaId { get; set; } = string.Empty;
    public string Status { get; set; } = "DISPATCHED";
    public string CorrelationId { get; set; } = string.Empty;
    public int CandidatesCount { get; set; }
    public IEnumerable<OrchestratedOfferDto> CandidatesNotified { get; set; } = new List<OrchestratedOfferDto>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
