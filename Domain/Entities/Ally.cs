using System;

namespace ManiDispatch.Domain.Entities;

public class Ally
{
    public Guid Id { get; set; }
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string EstadoVerificacion { get; set; } = string.Empty;

    // Métricas reales de Core que Rules usa para ordenar (SCRUM-1462). Sin calificaciones, el promedio es null.
    public decimal? CalificacionPromedio { get; set; }
    public int TotalCalificaciones { get; set; }
    public int? ZonasCobertura { get; set; }
}
