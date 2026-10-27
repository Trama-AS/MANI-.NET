using System.Net;
using System.Text;
using System.Text.Json;

namespace ManiDispatch.Tests.Helpers;

/// <summary>
/// Core simulado con estado, sobre un servidor HTTP real en localhost. Cumple el contrato que Dispatch
/// consume (GET allies/eligible, GET solicitudes/{id}/context, POST/GET notifications y el comando
/// POST solicitudes/{id}/asignacion de la decisión 6A), siempre acotado al tenant. El aliado del comando sale del
/// claim <c>sub</c> del JWT reenviado, como en Core.
/// </summary>
public sealed class FakeCoreServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<(Guid Tenant, Guid Id, Guid Zona, Guid Categoria, string Estado)> _solicitudes = new();
    private readonly Dictionary<Guid, Guid> _assignedTo = new();
    private readonly Dictionary<Guid, Guid> _allyOfUser = new();
    private readonly List<string> _eligibleQueries = new();
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

    public Guid AddSolicitud(Guid tenant, string estado = "PENDIENTE")
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

    /// <summary>Aliado (perfil) que corresponde al usuario del JWT en el comando de asignación.</summary>
    public void RegisterAllyUser(Guid usuarioId, Guid aliadoId)
    {
        lock (_gate) _allyOfUser[usuarioId] = aliadoId;
    }

    public Guid? AssignedTo(Guid requestId)
    {
        lock (_gate) return _assignedTo.TryGetValue(requestId, out var a) ? a : null;
    }

    public IReadOnlyList<string> EligibleQueries
    {
        get { lock (_gate) return _eligibleQueries.ToList(); }
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
            result = Route(ctx.Request.HttpMethod, path, query, hasTenant, tenant, body, UserFromToken(ctx.Request.Headers["Authorization"]));
        }

        ctx.Response.StatusCode = result.Status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result.Payload ?? new { }));
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private (int, object?) Route(string method, string path, System.Collections.Specialized.NameValueCollection query,
        bool hasTenant, Guid tenant, string body, Guid? usuario)
    {
        if (!hasTenant) return (400, new { error = "X-Tenant-Id requerido" });

        if (method == "GET" && path == "/api/v1/allies/eligible")
        {
            _eligibleQueries.Add(query.ToString() ?? string.Empty);
            var excluded = (query["excludeAllyIds"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var allies = _allies.Where(a => a.Tenant == tenant && !excluded.Contains(a.Id.ToString()))
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
            var esMia = usuario.HasValue && _assignedTo.TryGetValue(s.Id, out var asignado) && asignado == AllyOf(usuario.Value);
            return (200, new { id = s.Id, tenantId = s.Tenant, zonaId = s.Zona, categoriaId = s.Categoria, estado = s.Estado, esMia });
        }

        if (method == "POST" && path.StartsWith("/api/v1/solicitudes/") && path.EndsWith("/asignacion"))
        {
            if (!usuario.HasValue) return (401, new { error = "Sin sesión", code = "MANI-SOL-401" });
            var idText = path["/api/v1/solicitudes/".Length..^"/asignacion".Length];
            var index = _solicitudes.FindIndex(x => x.Tenant == tenant && x.Id.ToString() == idText);
            if (index < 0) return (404, new { error = "No encontrada", code = "MANI-SOL-404" });

            var s = _solicitudes[index];
            var aliado = AllyOf(usuario.Value);
            if (_assignedTo.TryGetValue(s.Id, out var ganador))
            {
                return ganador == aliado
                    ? (200, new { solicitud = new { id = s.Id, estado = s.Estado, aliadoId = aliado }, asignada = false })
                    : (409, new { error = "La solicitud ya no está disponible", code = "MANI-SOL-409" });
            }
            if (s.Estado != "PENDIENTE") return (409, new { error = "Estado no válido", code = "MANI-SOL-409E" });

            _assignedTo[s.Id] = aliado;
            _solicitudes[index] = (s.Tenant, s.Id, s.Zona, s.Categoria, "ASIGNADA");
            return (200, new { solicitud = new { id = s.Id, estado = "ASIGNADA", aliadoId = aliado }, asignada = true });
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

    private Guid AllyOf(Guid usuario) => _allyOfUser.TryGetValue(usuario, out var aliado) ? aliado : usuario;

    // Core valida la firma; el doble solo lee el claim sub del payload.
    private static Guid? UserFromToken(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ")) return null;
        var parts = authorization["Bearer ".Length..].Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("sub", out var sub) && Guid.TryParse(sub.GetString(), out var id) ? id : null;
    }

    public void Dispose() => _listener.Close();
}
