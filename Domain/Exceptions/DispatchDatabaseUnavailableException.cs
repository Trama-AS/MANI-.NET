using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>
/// La base de Dispatch no está configurada o falló (ADR-0016). Hereda de InvalidOperationException por
/// compatibilidad; los controladores la atrapan antes y responden 503 DATABASE_UNAVAILABLE.
/// </summary>
public class DispatchDatabaseUnavailableException : InvalidOperationException
{
    public DispatchDatabaseUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
