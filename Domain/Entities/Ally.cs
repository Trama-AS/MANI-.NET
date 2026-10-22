using System;

namespace ManiDispatch.Domain.Entities;

public class Ally
{
    public Guid Id { get; set; }
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string EstadoVerificacion { get; set; } = string.Empty;
}
