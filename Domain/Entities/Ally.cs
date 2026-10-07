namespace ManiDispatch.Domain.Entities;

public class Ally
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string? Telefono { get; set; }
    public decimal? CalificacionPromedio { get; set; }
    public decimal? DistanceKm { get; set; }
    public int? EstimatedArrivalMinutes { get; set; }
}
