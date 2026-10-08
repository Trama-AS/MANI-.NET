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
builder.Services.AddScoped<ISolicitudAssignmentRepository, PostgresSolicitudAssignmentRepository>();
builder.Services.AddScoped<GetEligibleAlliesUseCase>();
builder.Services.AddScoped<OrchestrateDispatchUseCase>();
builder.Services.AddScoped<AcceptDispatchOfferUseCase>();
builder.Services.AddScoped<RejectDispatchOfferUseCase>();

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

app.MapControllers();

app.Run();
