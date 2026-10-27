using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>
/// Core rechazó la sesión reenviada (401 o 403). No es una caída de Core: el controlador responde 403 con el código
/// que dio Core, no 503 (T2/T5).
/// </summary>
public class CoreAccessDeniedException : Exception
{
    public string Code { get; }

    public CoreAccessDeniedException(string message, string? code = null) : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "MANI-DSP-403" : code;
    }
}
