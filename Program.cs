using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Core;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();
builder.Services.AddHttpClient<IAllyRepository, CoreAllyClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>();
builder.Services.AddHttpClient<IDispatchPublisher, CoreDispatchPublisher>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>();
builder.Services.AddScoped<ISolicitudAssignmentRepository, PostgresSolicitudAssignmentRepository>();
builder.Services.AddScoped<GetEligibleAlliesUseCase>();
builder.Services.AddScoped<OrchestrateDispatchUseCase>();
builder.Services.AddScoped<AcceptDispatchOfferUseCase>();
builder.Services.AddScoped<RejectDispatchOfferUseCase>();
builder.Services.AddScoped<GetAllyAssignmentsUseCase>();

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

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }

