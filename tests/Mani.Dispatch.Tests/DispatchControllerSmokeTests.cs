using System.Text.Json;
using Mani.Dispatch.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Mani.Dispatch.Tests;

/// <summary>
/// Prueba de humo (QA-06 / SCRUM-1123): confirma que el proyecto de pruebas compila contra el
/// servicio, se descubre y se ejecuta, y que el reporte de cobertura se genera. Cuando lleguen
/// el emparejamiento y la exclusión concurrente reales, sus pruebas se suman en esta carpeta.
/// </summary>
public class DispatchControllerSmokeTests
{
    private static DispatchController CreateController(string? correlationId = null)
    {
        var httpContext = new DefaultHttpContext();
        if (correlationId is not null)
        {
            httpContext.Request.Headers["X-Correlation-ID"] = correlationId;
        }

        return new DispatchController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Fact]
    public void HealthCheck_ReturnsOk_WithServiceUpAndEchoedCorrelationId()
    {
        var controller = CreateController("corr-123");

        var result = controller.HealthCheck();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("UP", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MANI-Dispatch-DotNet", json.RootElement.GetProperty("service").GetString());
        Assert.Equal("corr-123", json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void HealthCheck_WithoutCorrelationId_StillReturnsOk()
    {
        var controller = CreateController();

        var result = controller.HealthCheck();

        Assert.IsType<OkObjectResult>(result);
    }
}
