using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>
/// Rules no pudo ordenar: no respondió, respondió con error o no está configurado, o no se pudo leer el criterio del
/// tenant. El controlador responde 503 y no se presenta otro orden en su lugar (T2, SCRUM-1462).
/// </summary>
public class RulesServiceUnavailableException : Exception
{
    public RulesServiceUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
