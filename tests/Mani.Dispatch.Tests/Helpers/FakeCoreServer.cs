using System.Net;
using System.Text;
using System.Text.Json;

namespace ManiDispatch.Tests.Helpers;

/// <summary>
/// Core simulado con estado, sobre un servidor HTTP real en localhost. Cumple el contrato que Dispatch
/// consume (GET allies/eligible, GET solicitudes/{id}/context, POST/GET notifications), siempre acotado al tenant.
/// </summary>
public sealed class FakeCoreServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<(Guid Tenant, Guid Id, Guid Zona, Guid Categoria, string Estado)> _solicitudes = new();
    private readonly List<(Guid Tenant, Guid Id, string Nombre)> _allies = new();
    private readonly List<(Guid Tenant, JsonElement Body)> _notifications = new();

    public string BaseUrl { get; }
    public bool FailNotifications { get; set; }

    public FakeCoreServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 40000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
            }
        }
        _ = Task.Run(Loop);
    }

    public Guid AddSolicitud(Guid tenant, string estado = "PUBLICADA")
    {
        var id = Guid.NewGuid();
        lock (_gate) _solicitudes.Add((tenant, id, Guid.NewGuid(), Guid.NewGuid(), estado));
        return id;
    }

    public Guid AddEligibleAlly(Guid tenant, string nombre)
    {
        var id = Guid.NewGuid();
        lock (_gate) _allies.Add((tenant, id, nombre));
        return id;
    }

    public int CountOffers(Guid tenant, Guid requestId)
    {
        lock (_gate)
        {
            return _notifications.Count(n => n.Tenant == tenant &&
                n.Body.GetProperty("payload").GetProperty("requestId").GetString() == requestId.ToString());
        }
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                await Handle(ctx);
            }
            catch
            {
                return;
            }
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
        var tenantHeader = ctx.Request.Headers["X-Tenant-Id"];
        var hasTenant = Guid.TryParse(tenantHeader, out var tenant);
        using var reader = new StreamReader(ctx.Request.InputStream);
        var body = await reader.ReadToEndAsync();

        (int Status, object? Payload) result;
        lock (_gate)
        {
            result = Route(ctx.Request.HttpMethod, path, query, hasTenant, tenant, body);
        }

        ctx.Response.StatusCode = result.Status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result.Payload ?? new { }));
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private (int, object?) Route(string method, string path, System.Collections.Specialized.NameValueCollection query,
        bool hasTenant, Guid tenant, string body)
    {
        if (!hasTenant) return (400, new { error = "X-Tenant-Id requerido" });

        if (method == "GET" && path == "/api/v1/allies/eligible")
        {
            var allies = _allies.Where(a => a.Tenant == tenant)
                .Select(a => new { id = a.Id, nombreRazonSocial = a.Nombre, tipo = "PERSONA_NATURAL", estadoVerificacion = "VERIFICADO" })
                .ToList();
            return (200, new { allies, totalCount = allies.Count });
        }

        if (method == "GET" && path.StartsWith("/api/v1/solicitudes/") && path.EndsWith("/context"))
        {
            var idText = path["/api/v1/solicitudes/".Length..^"/context".Length];
            var found = _solicitudes.Where(s => s.Tenant == tenant && s.Id.ToString() == idText).ToList();
            if (found.Count == 0) return (404, new { error = "No encontrada" });
            var s = found[0];
            return (200, new { id = s.Id, tenantId = s.Tenant, zonaId = s.Zona, categoriaId = s.Categoria, estado = s.Estado });
        }

        if (method == "POST" && path == "/api/v1/notifications")
        {
            if (FailNotifications) return (503, new { error = "no disponible" });
            _notifications.Add((tenant, JsonDocument.Parse(body).RootElement.Clone()));
            return (201, new { ok = true });
        }

        if (method == "GET" && path == "/api/v1/notifications")
        {
            var requestId = query["requestId"];
            var offers = _notifications
                .Where(n => n.Tenant == tenant && n.Body.GetProperty("payload").GetProperty("requestId").GetString() == requestId)
                .Select(n => n.Body.GetProperty("payload").Clone())
                .ToList();
            return (200, offers);
        }

        return (404, new { error = "ruta desconocida" });
    }

    public void Dispose() => _listener.Close();
}
