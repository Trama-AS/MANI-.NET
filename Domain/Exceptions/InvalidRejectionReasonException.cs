using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>
/// El rechazo no trae un motivo válido (SCRUM-1617). Se responde 422 sin registrar nada:
/// <c>MANI-SOL-422M</c> si falta el motivo o no está en el catálogo de <c>solicitud_rechazo.motivo</c>, y
/// <c>MANI-SOL-422D</c> si el detalle no cumple (obligatorio para <c>OTRO</c>, prohibido para los demás).
/// </summary>
public class InvalidRejectionReasonException : ArgumentException
{
    public const string Code = "MANI-SOL-422M";
    public const string DetailCode = "MANI-SOL-422D";

    public InvalidRejectionReasonException(string message, string errorCode = Code) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
