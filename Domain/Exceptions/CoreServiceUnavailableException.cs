using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>
/// Core no respondió, respondió con error o no está configurado (ADR-0030). El controlador lo traduce a 503.
/// </summary>
public class CoreServiceUnavailableException : Exception
{
    public CoreServiceUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
