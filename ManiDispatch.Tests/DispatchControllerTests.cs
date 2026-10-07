using System.Collections.Generic;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class DispatchControllerTests
{
    private readonly Mock<IAllyRepository> _mockRepo;
    private readonly GetEligibleAlliesUseCase _useCase;
    private readonly DispatchController _controller;

    public DispatchControllerTests()
    {
        _mockRepo = new Mock<IAllyRepository>();
        _useCase = new GetEligibleAlliesUseCase(_mockRepo.Object);
        _controller = new DispatchController(_useCase)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        _controller.HttpContext.Request.Headers["X-Correlation-ID"] = "test-corr-id";
        _controller.HttpContext.Request.Headers["X-Tenant-Id"] = "tenant-001";
    }

    [Fact]
    public async Task MatchProfessionals_ReturnsOkResult_WithMatchedAllies()
    {
        // Arrange
        var allies = new List<Ally>
        {
            new() { Id = "ally-1", Nombre = "Camila Morales", CalificacionPromedio = 5.0m }
        };
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((allies, 1));

        var request = new MatchRequestDto
        {
            ZonaId = "zona-norte",
            CategoriaId = "cat-spa"
        };

        // Act
        var actionResult = await _controller.MatchProfessionals(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal("tenant-001", response.TenantId);
        Assert.Equal("zona-norte", response.ZonaId);
        Assert.Equal(1, response.TotalCount);
    }

    [Fact]
    public void AcceptRequest_ReturnsOkOnFirst_AndConflictOnSecond()
    {
        // Arrange
        string reqId = "req-unique-" + System.Guid.NewGuid().ToString();
        var dto1 = new AcceptRequestDto("ally-first");
        var dto2 = new AcceptRequestDto("ally-second");

        // Act 1: Primer aliado acepta
        var result1 = _controller.AcceptRequest(reqId, dto1);

        // Act 2: Segundo aliado intenta aceptar la misma solicitud (colisión concurrente)
        var result2 = _controller.AcceptRequest(reqId, dto2);

        // Assert
        Assert.IsType<OkObjectResult>(result1);
        var conflictResult = Assert.IsType<ConflictObjectResult>(result2);
        Assert.NotNull(conflictResult.Value);
    }
}
