using System;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1724 (QAS-23): cobertura del caso de uso «rechazar oferta». Fija sus validaciones previas: identificador
/// válido y aliado VERIFICADO. Ninguna de ellas registra un rechazo (los mocks estrictos fallan ante una llamada).
/// </summary>
public class RejectDispatchOfferUseCaseTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid UsuarioId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid AllyId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private const string RequestId = "44444444-4444-4444-8444-444444444444";
    private const string Motivo = "FUERA_DE_ZONA";

    private readonly Mock<ISolicitudAssignmentRepository> _assignments = new(MockBehavior.Strict);
    private readonly Mock<IAllyRepository> _allies = new(MockBehavior.Strict);

    private RejectDispatchOfferUseCase Build() => new(_assignments.Object, _allies.Object);

    private void AllyIs(string? estado)
    {
        AllyProfile? perfil = estado == null ? null : new AllyProfile(AllyId, TenantId, UsuarioId, estado);
        _allies.Setup(a => a.GetAllyProfileAsync(UsuarioId, TenantId)).ReturnsAsync(perfil);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        Assert.Throws<ArgumentNullException>(() => new RejectDispatchOfferUseCase(null!, _allies.Object));
        Assert.Throws<ArgumentNullException>(() => new RejectDispatchOfferUseCase(_assignments.Object, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task EmptyRequestId_IsRejected_BeforeTouchingAnyDependency(string? requestId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Build().ExecuteAsync(requestId!, TenantId, UsuarioId, Motivo));

        Assert.Equal("requestId", ex.ParamName);
        Assert.Contains("obligatorio", ex.Message);
        _allies.VerifyNoOtherCalls();
        _assignments.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("no-es-un-uuid")]
    [InlineData("44444444-4444-4444-8444-44444444444")]
    public async Task NotAUuid_IsRejected_AndTheMessageSaysWhichValue(string requestId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Build().ExecuteAsync(requestId, TenantId, UsuarioId, Motivo));

        Assert.Equal("requestId", ex.ParamName);
        Assert.Contains(requestId, ex.Message);
        _allies.VerifyNoOtherCalls();
        _assignments.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UserWithoutAllyProfile_GetsForbidden_AndNothingIsRegistered()
    {
        AllyIs(null);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build().ExecuteAsync(RequestId, TenantId, UsuarioId, Motivo));

        Assert.StartsWith("MANI-SOL-403V", ex.Message);
        Assert.Contains("no existe un perfil de aliado", ex.Message);
        _assignments.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("PENDIENTE")]
    [InlineData("RECHAZADO")]
    [InlineData("")]
    public async Task AllyNotVerified_GetsForbidden_AndNothingIsRegistered(string estado)
    {
        AllyIs(estado);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build().ExecuteAsync(RequestId, TenantId, UsuarioId, Motivo));

        Assert.StartsWith("MANI-SOL-403V", ex.Message);
        Assert.Contains("no está verificado", ex.Message);
        _assignments.VerifyNoOtherCalls();
    }
}
