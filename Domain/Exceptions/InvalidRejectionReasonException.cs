using System;

namespace ManiDispatch.Domain.Exceptions;

/// <summary>El motivo de rechazo no está en el catálogo de <c>solicitud_rechazo.motivo</c> (N-1). Se responde 422.</summary>
public class InvalidRejectionReasonException : ArgumentException
{
    public const string Code = "MANI-SOL-422M";

    public InvalidRejectionReasonException(string message) : base(message)
    {
    }
}
