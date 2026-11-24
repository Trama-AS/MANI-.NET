using System;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1727 (QAS-23): cobertura del caso de uso «aceptar oferta». Cada prueba fija una de sus decisiones: validar el
/// identificador, exigir un aliado VERIFICADO, impedir aceptar lo que ya se rechazó y delegar en Core, que es quien
/// registra la asignación.
/// </summary>
public class AcceptDispatchOfferUseCaseTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid UsuarioId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid AllyId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid RequestId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private readonly Mock<ISolicitudAssignmentRepository> _assignments = new(MockBehavior.Strict);
    private readonly Mock<IAllyRepository> _allies = new(MockBehavior.Strict);
    private readonly Mock<ICoreAssignmentClient> _core = new(MockBehavior.Strict);

    private AcceptDispatchOfferUseCase Build() => new(_assignments.Object, _allies.Object, _core.Object);

    private void AllyIs(string? estado)
    {
        AllyProfile? perfil = estado == null ? null : new AllyProfile(AllyId, TenantId, UsuarioId, estado);
        _allies.Setup(a => a.GetAllyProfileAsync(UsuarioId, TenantId)).ReturnsAsync(perfil);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        Assert.Throws<ArgumentNullException>(() => new AcceptDispatchOfferUseCase(null!, _allies.Object, _core.Object));
        Assert.Throws<ArgumentNullException>(() => new AcceptDispatchOfferUseCase(_assignments.Object, null!, _core.Object));
        Assert.Throws<ArgumentNullException>(() => new AcceptDispatchOfferUseCase(_assignments.Object, _allies.Object, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task EmptyRequestId_IsRejected_BeforeTouchingAnyDependency(string? requestId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Build().ExecuteAsync(requestId!, TenantId, UsuarioId));

        Assert.Equal("requestId", ex.ParamName);
        Assert.Contains("obligatorio", ex.Message);
        _allies.VerifyNoOtherCalls();
        _core.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("no-es-un-uuid")]
    [InlineData("1234")]
    [InlineData("44444444-4444-4444-8444-44444444444")]
    public async Task NotAUuid_IsRejected_AndTheMessageSaysWhichValue(string requestId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Build().ExecuteAsync(requestId, TenantId, UsuarioId));

        Assert.Equal("requestId", ex.ParamName);
        Assert.Contains(requestId, ex.Message);
        _allies.VerifyNoOtherCalls();
        _core.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UserWithoutAllyProfile_GetsForbidden_AndCoreIsNeverCalled()
    {
        AllyIs(null);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId));

        Assert.StartsWith("MANI-SOL-403V", ex.Message);
        _core.VerifyNoOtherCalls();
        _assignments.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("PENDIENTE")]
    [InlineData("RECHAZADO")]
    [InlineData("")]
    public async Task AllyNotVerified_GetsForbidden_AndCoreIsNeverCalled(string estado)
    {
        AllyIs(estado);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId));

        Assert.StartsWith("MANI-SOL-403V", ex.Message);
        Assert.Contains("no está verificado", ex.Message);
        _core.VerifyNoOtherCalls();
        _assignments.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("VERIFICADO")]
    [InlineData("verificado")]
    [InlineData("Verificado")]
    public async Task VerifiedAlly_IsAccepted_WhateverTheCasing(string estado)
    {
        AllyIs(estado);
        _assignments.Setup(a => a.HasRejectedAsync(RequestId, TenantId, UsuarioId, AllyId)).ReturnsAsync(false);
        var esperado = new AssignmentResult(AssignmentStatus.Assigned, AllyId, null);
        _core.Setup(c => c.AssignAsync(RequestId, TenantId, "clave-1", "corr-1")).ReturnsAsync(esperado);

        var result = await Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId, "clave-1", "corr-1");

        Assert.Same(esperado, result);
    }

    [Fact]
    public async Task AllyWhoAlreadyRejected_CannotAcceptLater_AndCoreIsNeverCalled()
    {
        AllyIs("VERIFICADO");
        _assignments.Setup(a => a.HasRejectedAsync(RequestId, TenantId, UsuarioId, AllyId)).ReturnsAsync(true);

        var result = await Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId);

        Assert.Equal(AssignmentStatus.RejectedBefore, result.Status);
        Assert.Equal("MANI-DSP-409R", result.Code);
        Assert.Null(result.AssignedAllyId);
        _core.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AssignmentStatus.AlreadyAssigned)]
    [InlineData(AssignmentStatus.Conflict)]
    [InlineData(AssignmentStatus.NotFound)]
    [InlineData(AssignmentStatus.InvalidState)]
    [InlineData(AssignmentStatus.Forbidden)]
    public async Task WhateverCoreDecides_IsReturnedUntouched(AssignmentStatus status)
    {
        AllyIs("VERIFICADO");
        _assignments.Setup(a => a.HasRejectedAsync(RequestId, TenantId, UsuarioId, AllyId)).ReturnsAsync(false);
        var desdeCore = new AssignmentResult(status, null, "mensaje de Core", "CODIGO-CORE");
        _core.Setup(c => c.AssignAsync(RequestId, TenantId, null, null)).ReturnsAsync(desdeCore);

        var result = await Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId);

        Assert.Same(desdeCore, result);
    }

    [Fact]
    public async Task TheTenantAndUserComeFromTheSession_AndTheIdempotencyKeyReachesCore()
    {
        AllyIs("VERIFICADO");
        _assignments.Setup(a => a.HasRejectedAsync(RequestId, TenantId, UsuarioId, AllyId)).ReturnsAsync(false);
        _core.Setup(c => c.AssignAsync(RequestId, TenantId, "k-9", null))
            .ReturnsAsync(new AssignmentResult(AssignmentStatus.AlreadyAssigned, AllyId, null));

        var result = await Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId, "k-9");

        Assert.Equal(AssignmentStatus.AlreadyAssigned, result.Status);
        _core.Verify(c => c.AssignAsync(RequestId, TenantId, "k-9", null), Times.Once);
    }

    [Fact]
    public async Task ACoreFailure_Propagates_AndNothingIsReportedAsAssigned()
    {
        AllyIs("VERIFICADO");
        _assignments.Setup(a => a.HasRejectedAsync(RequestId, TenantId, UsuarioId, AllyId)).ReturnsAsync(false);
        _core.Setup(c => c.AssignAsync(RequestId, TenantId, null, null)).ThrowsAsync(new TimeoutException("Core no respondió"));

        await Assert.ThrowsAsync<TimeoutException>(() => Build().ExecuteAsync(RequestId.ToString(), TenantId, UsuarioId));
    }
}
