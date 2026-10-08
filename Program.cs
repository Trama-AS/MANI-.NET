using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Dispatch;
using ManiDispatch.Infrastructure.Repositories;

// Cargar variables de entorno si existe .env
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Configurar puerto 5000
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);
});

builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// Inyección de dependencias para Arquitectura Limpia
builder.Services.AddScoped<IAllyRepository, PostgresAllyRepository>();
builder.Services.AddScoped<IDispatchPublisher, PostgresDispatchPublisher>();
builder.Services.AddScoped<GetEligibleAlliesUseCase>();
builder.Services.AddScoped<OrchestrateDispatchUseCase>();

var app = builder.Build();

app.UseCors();

// Middleware de Trazabilidad con X-Correlation-ID
app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue("X-Correlation-ID", out var correlationId))
    {
        correlationId = Guid.NewGuid().ToString();
    }
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    await next();
});

// Endpoint de Healthcheck rápido
app.MapGet("/health", (HttpContext ctx) =>
{
    var correlationId = ctx.Response.Headers["X-Correlation-ID"].ToString();
    return Results.Ok(new
    {
        status = "UP",
        service = "MANI-Dispatch-DotNet",
        timestamp = DateTime.UtcNow.ToString("o"),
        correlationId
    });
});

app.MapControllers();

app.Run();
