using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Rules;
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
// Decisión 6A (SCRUM-1403): Core registra la asignación; Dispatch le envía el comando con el JWT del aliado.
builder.Services.AddHttpClient<ICoreAssignmentClient, CoreAssignmentClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>();
// Orden de los elegibles (SCRUM-1462): criterio del tenant en Core y evaluación en Rules, sin orden local si fallan.
builder.Services.AddHttpClient<ITenantRulesReader, CoreTenantRulesClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>();
builder.Services.AddHttpClient<IRulesClient, RulesClient>(client => client.Timeout = TimeSpan.FromSeconds(3));
// Contexto efectivo del administrador de plataforma (SCRUM-901): lo resuelve Core.
builder.Services.AddHttpClient<ICoreContextoClient, CoreContextoClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler<ForwardAuthorizationHandler>();
builder.Services.AddScoped<ISolicitudAssignmentRepository, PostgresSolicitudAssignmentRepository>();
builder.Services.AddScoped<GetEligibleAlliesUseCase>();
builder.Services.AddScoped<OrchestrateDispatchUseCase>();
builder.Services.AddScoped<AcceptDispatchOfferUseCase>();
builder.Services.AddScoped<RejectDispatchOfferUseCase>();
builder.Services.AddScoped<GetAllyAssignmentsUseCase>();
builder.Services.AddScoped<GetAllyOffersUseCase>();

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

// SCRUM-901: sesión efectiva (tenant elegido / actuar como) antes de los controladores.
app.UseMiddleware<ContextoPlataformaMiddleware>();

app.MapControllers();

app.Run();

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }

