using System;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// Un token alterado, expirado o firmado con otra clave debe dar 401 en todos los
/// endpoints protegidos, sin llegar a ningún caso de uso.
/// </summary>
[Collection("JwtAuthEnvironment")]
public class DispatchControllerTokenTests
{
    private readonly Mock<IAllyRepository> _repo = new(MockBehavior.Strict);
    private readonly Mock<IDispatchPublisher> _publisher = new(MockBehavior.Strict);
    private readonly InMemorySolicitudAssignmentRepository _assignments = new();
    private readonly DispatchController _controller;
    private readonly Guid _tenantId = Guid.NewGuid();

    public DispatchControllerTokenTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        _controller = new DispatchController(
            new GetEligibleAlliesUseCase(_repo.Object),
            new OrchestrateDispatchUseCase(_repo.Object, _publisher.Object),
            new AcceptDispatchOfferUseCase(_assignments, _repo.Object),
            new RejectDispatchOfferUseCase(_assignments, _repo.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public static TheoryData<string> BadTokenKinds => new() { "alterado", "expirado", "otra-firma" };

    private string BuildBadToken(string kind)
    {
        var userId = Guid.NewGuid().ToString();
        switch (kind)
        {
            case "expirado":
                return TestTokenHelper.CreateTestToken(
                    _tenantId, userId, "aliado", expires: DateTime.UtcNow.AddMinutes(-30));
            case "otra-firma":
                return TestTokenHelper.CreateTestToken(
                    _tenantId, userId, "aliado", secret: "otra-clave-distinta-de-al-menos-32-caracteres!!");
            default:
                var parts = TestTokenHelper.CreateTestToken(_tenantId, userId, "aliado").Split('.');
                return $"{parts[0]}.{parts[1]}xyz.{parts[2]}";
        }
    }

    private void UseToken(string kind) =>
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {BuildBadToken(kind)}";

    private static void AssertUnauthorized(IActionResult result)
    {
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthorized.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BadTokenKinds))]
    public async Task AcceptRequest_WithBadToken_Returns401(string kind)
    {
        UseToken(kind);
        AssertUnauthorized(await _controller.AcceptRequest(Guid.NewGuid().ToString()));
    }

    [Theory]
    [MemberData(nameof(BadTokenKinds))]
    public async Task RejectRequest_WithBadToken_Returns401(string kind)
    {
        UseToken(kind);
        AssertUnauthorized(await _controller.RejectRequest(Guid.NewGuid().ToString(), null));
    }

    [Theory]
    [MemberData(nameof(BadTokenKinds))]
    public async Task OrchestrateDispatch_WithBadToken_Returns401(string kind)
    {
        UseToken(kind);
        AssertUnauthorized(await _controller.OrchestrateDispatch(new OrchestrateRequestDto(Guid.NewGuid().ToString())));
    }

    [Theory]
    [MemberData(nameof(BadTokenKinds))]
    public async Task GetEligibleAlliesForRequest_WithBadToken_Returns401(string kind)
    {
        UseToken(kind);
        AssertUnauthorized(await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), 1, 10));
    }
}
