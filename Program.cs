using System.Collections.Generic;
using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Dispatch;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Infrastructure.Resilience;
using ManiDispatch.Infrastructure.Rules;
using Microsoft.Extensions.Logging;

// Cargar variables de entorno si existe .env
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

// SCRUM-1658 (T-21 / QAS-22): logs estructurados JSON con scopes (incluyendo correlationId y service).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// TEC-06 (SCRUM-1453): sin la URL de Core, Dispatch no arranca. Antes arrancaba y cada petición de elegibilidad,
// ofertas o asignación fallaba después con 503; ahora el error sale al iniciar y dice qué variable falta.
UrlServicioCore.Exigir(builder.Configuration);

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

// Inyección de dependencias para Arquitectura Limpia y Circuit Breakers (SCRUM-1663 / QAS-40)
var coreCircuitBreaker = new CircuitBreakerPolicy("mani-core-node", failureThreshold: 5, breakDuration: TimeSpan.FromSeconds(10));
var rulesCircuitBreaker = new CircuitBreakerPolicy("mani-rules-java", failureThreshold: 5, breakDuration: TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(coreCircuitBreaker);
builder.Services.AddSingleton(rulesCircuitBreaker);

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();
builder.Services.AddHttpClient<IAllyRepository, CoreAllyClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(coreCircuitBreaker));
builder.Services.AddHttpClient<IDispatchPublisher, CoreDispatchPublisher>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(coreCircuitBreaker));
// Decisión 6A (SCRUM-1403): Core registra la asignación; Dispatch le envía el comando con el JWT del aliado.
builder.Services.AddHttpClient<ICoreAssignmentClient, CoreAssignmentClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(coreCircuitBreaker));
// Orden de los elegibles (SCRUM-1462): criterio del tenant en Core y evaluación en Rules, sin orden local si fallan.
builder.Services.AddHttpClient<ITenantRulesReader, CoreTenantRulesClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(coreCircuitBreaker));
builder.Services.AddHttpClient<IRulesClient, RulesClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(rulesCircuitBreaker));
// Contexto efectivo del administrador de plataforma (SCRUM-901): lo resuelve Core.
builder.Services.AddHttpClient<ICoreContextoClient, CoreContextoClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>()
    .AddHttpMessageHandler(() => new CircuitBreakerDelegatingHandler(coreCircuitBreaker));
builder.Services.AddScoped<ISolicitudAssignmentRepository, PostgresSolicitudAssignmentRepository>();
// SCRUM-1468: fuera de Development, Dispatch no arranca si su conexión usa un rol que salta RLS.
builder.Services.AddHostedService<VerificacionRolServicio>();
builder.Services.AddScoped<GetEligibleAlliesUseCase>();
builder.Services.AddScoped<OrchestrateDispatchUseCase>();
builder.Services.AddScoped<AcceptDispatchOfferUseCase>();
builder.Services.AddScoped<RejectDispatchOfferUseCase>();
builder.Services.AddScoped<GetAllyAssignmentsUseCase>();
builder.Services.AddScoped<GetAllyOffersUseCase>();

var app = builder.Build();

app.UseCors();

// SCRUM-1658 (T-21 / QAS-22): Middleware de trazabilidad con X-Correlation-ID único y log JSON estructurado.
app.Use(async (context, next) =>
{
    var raw = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlationId = string.IsNullOrWhiteSpace(raw) ? $"disp-{Guid.NewGuid():N}" : raw.Trim();
    context.Request.Headers["X-Correlation-ID"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    using (app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["service"] = "mani-dispatch-dotnet",
        ["correlationId"] = correlationId,
        ["correlation_id"] = correlationId
    }))
    {
        app.Logger.LogInformation(
            "HTTP {Method} {Path} iniciada (correlationId={CorrelationId})",
            context.Request.Method,
            context.Request.Path.Value,
            correlationId);
        await next();
    }
});

// SCRUM-901: sesión efectiva (tenant elegido / actuar como) antes de los controladores.
app.UseMiddleware<ContextoPlataformaMiddleware>();

app.MapControllers();

app.Run();

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }
