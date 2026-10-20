using System;

namespace ManiDispatch.Domain.Entities;

public class SolicitudContext
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ZonaId { get; set; }
    public Guid CategoriaId { get; set; }
    public string Estado { get; set; } = string.Empty;
}
