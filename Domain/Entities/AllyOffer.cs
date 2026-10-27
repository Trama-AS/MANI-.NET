using System;
using System.Text.Json;

namespace ManiDispatch.Domain.Entities;

/// <summary>
/// Elemento de la bandeja del aliado tal como lo entrega Core (GET /api/v1/allies/me/offers). La dirección y la
/// fecha de asignación solo vienen cuando la solicitud ya es del aliado (<see cref="EsMia"/>).
/// </summary>
public class AllyOffer
{
    public Guid Id { get; set; }
    public string Estado { get; set; } = string.Empty;
    public bool EsMia { get; set; }
    public string? Categoria { get; set; }
    public string? FlujoOperativo { get; set; }
    public string? Descripcion { get; set; }
    public int Fotos { get; set; }
    public string? Zona { get; set; }
    public string? ZonaPadre { get; set; }
    public JsonElement? ReglasSitio { get; set; }
    public string? Direccion { get; set; }
    public DateTimeOffset? CreadaEn { get; set; }
    public DateTimeOffset? AsignadaEn { get; set; }
}
