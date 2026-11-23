using System.Collections.Generic;

namespace ManiDispatch.Domain.Entities;

/// <summary>
/// Lo que Core devuelve en una sola lectura para listar los elegibles de una solicitud (SCRUM-1652): su contexto, una
/// página de elegibles (sin descontar rechazos, que son de Dispatch por ADR-0030), el total y el criterio de orden del
/// tenant. <see cref="CriterioOrdenListado"/> es null si la regla propia del tenant no lo trae.
/// </summary>
public class EligibleForRequest
{
    public SolicitudContext Solicitud { get; set; } = new();
    public string? CriterioOrdenListado { get; set; }
    public List<Ally> Allies { get; set; } = new();
    public int TotalCount { get; set; }
}
