using System;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Exceptions;

namespace ManiDispatch.Application.UseCases;

/// <summary>
/// Rechazo de una oferta por el aliado (SCRUM-1404). El rechazo es dominio de Dispatch: se guarda en
/// <c>solicitud_rechazo</c> y saca la solicitud de la bandeja del aliado y de la siguiente orquestación.
/// </summary>
public class RejectDispatchOfferUseCase
{
    // Catálogo del CHECK de solicitud_rechazo.motivo. El motivo es obligatorio (decisión del PO, SCRUM-1617): el
    // servidor nunca inventa ni omite uno. OTRO exige un detalle en texto libre (migración 0025 de Core).
    public static readonly string[] MotivosRechazo = { "FUERA_DE_ZONA", "SIN_DISPONIBILIDAD", "NO_ES_MI_ESPECIALIDAD", "OTRO" };
    public const string MotivoOtro = "OTRO";
    public const int DetalleMinimo = 3;
    public const int DetalleMaximo = 280;

    private readonly ISolicitudAssignmentRepository _assignmentRepository;
    private readonly IAllyRepository _allyRepository;

    public RejectDispatchOfferUseCase(ISolicitudAssignmentRepository assignmentRepository, IAllyRepository allyRepository)
    {
        _assignmentRepository = assignmentRepository ?? throw new ArgumentNullException(nameof(assignmentRepository));
        _allyRepository = allyRepository ?? throw new ArgumentNullException(nameof(allyRepository));
    }

    public async Task<RejectionResult> ExecuteAsync(
        string requestId,
        Guid tenantId,
        Guid usuarioId,
        string? motivo = null,
        string? detalle = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("El identificador de solicitud (RequestId) es obligatorio.", nameof(requestId));
        }

        if (!Guid.TryParse(requestId, out var requestGuid))
        {
            throw new ArgumentException($"El RequestId '{requestId}' no es un UUID válido.", nameof(requestId));
        }

        // Se valida antes de consultar a Core o a la base: un rechazo inválido responde 422, nunca 503, y no
        // registra nada (C1, C4).
        var (motivoNormalizado, detalleNormalizado) = ValidarMotivo(motivo, detalle);

        var allyProfile = await _allyRepository.GetAllyProfileAsync(usuarioId, tenantId);
        if (allyProfile == null)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: no existe un perfil de aliado asociado a la cuenta en este tenant.");
        }

        bool isVerified = string.Equals(allyProfile.EstadoVerificacion, "VERIFICADO", StringComparison.OrdinalIgnoreCase);

        if (!isVerified)
        {
            throw new UnauthorizedAccessException("MANI-SOL-403V: tu registro como aliado aún no está verificado.");
        }

        // Core solo entrega el contexto al aliado con oferta propia o asignado: si no, 404 sin revelar que existe (N-2).
        var solicitud = await _allyRepository.GetSolicitudContextAsync(requestGuid, tenantId);
        if (solicitud == null)
        {
            return new RejectionResult(RejectionStatus.NotFound, motivoNormalizado, "MANI-DSP-404");
        }

        if (solicitud.EsMia)
        {
            return new RejectionResult(RejectionStatus.AlreadyMine, motivoNormalizado, "MANI-DSP-409A");
        }

        if (!string.Equals(solicitud.Estado, "PENDIENTE", StringComparison.OrdinalIgnoreCase))
        {
            return new RejectionResult(RejectionStatus.InvalidState, motivoNormalizado, "MANI-DSP-409E");
        }

        var registrado = await _assignmentRepository.RejectRequestAsync(
            requestGuid, tenantId, usuarioId, allyProfile.Id, motivoNormalizado, detalleNormalizado);
        return new RejectionResult(
            registrado ? RejectionStatus.Rejected : RejectionStatus.AlreadyRejected, motivoNormalizado, Detalle: detalleNormalizado);
    }

    private static (string Motivo, string? Detalle) ValidarMotivo(string? motivo, string? detalle)
    {
        var permitidos = string.Join(", ", MotivosRechazo);
        if (string.IsNullOrWhiteSpace(motivo))
        {
            throw new InvalidRejectionReasonException(
                $"{InvalidRejectionReasonException.Code}: el motivo de rechazo es obligatorio. Valores permitidos: {permitidos}.");
        }

        var motivoNormalizado = motivo.Trim().ToUpperInvariant();
        if (Array.IndexOf(MotivosRechazo, motivoNormalizado) < 0)
        {
            throw new InvalidRejectionReasonException(
                $"{InvalidRejectionReasonException.Code}: el motivo '{motivo}' no es válido. Valores permitidos: {permitidos}.");
        }

        var detalleNormalizado = string.IsNullOrWhiteSpace(detalle) ? null : detalle.Trim();
        if (motivoNormalizado != MotivoOtro)
        {
            if (detalleNormalizado != null)
            {
                throw new InvalidRejectionReasonException(
                    $"{InvalidRejectionReasonException.DetailCode}: solo el motivo {MotivoOtro} lleva detalle.",
                    InvalidRejectionReasonException.DetailCode);
            }
            return (motivoNormalizado, null);
        }

        // Se cuentan caracteres (code points), igual que char_length en el CHECK de la base.
        var longitud = detalleNormalizado?.EnumerateRunes().Count() ?? 0;
        if (longitud < DetalleMinimo || longitud > DetalleMaximo)
        {
            throw new InvalidRejectionReasonException(
                $"{InvalidRejectionReasonException.DetailCode}: el motivo {MotivoOtro} exige un detalle de {DetalleMinimo} a {DetalleMaximo} caracteres.",
                InvalidRejectionReasonException.DetailCode);
        }
        return (motivoNormalizado, detalleNormalizado);
    }
}
