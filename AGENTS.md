# AGENTS.md — MANI-Dispatch-DotNet (Servicio de Despacho)

Bienvenido al repositorio **MANI-Dispatch-DotNet**. Este archivo define el rol y las directrices operativas de IA y desarrollo para el microservicio de Despacho y Emparejamiento del ecosistema MANI.

---

## 1. Rol y Responsabilidad del Repositorio
* **Tecnología:** C# / .NET 8 (ASP.NET Core Web API).
* **Puerto:** `5000` (interno).
* **Dominio Funcional:** Algoritmos de despacho y concurrencia:
  - **RF-12:** Emparejamiento inteligente de manicuristas por cercanía geográfica y disponibilidad.
  - **RF-14:** Gestión del ciclo de aceptación y rechazo de servicios por parte de profesionales.
  - **RNF-05:** **Exclusión Concurrente Atómica:** Garantizar que si múltiples profesionales intentan aceptar la misma solicitud simultáneamente, el primero gane (`200 OK`) y los siguientes reciban `409 Conflict` de inmediato.

---

## 2. Topología de Comunicación y API Gateway

```
[ MANI-Flutter ] 
       │
       ▼ (Peticiones al puerto 80)
[ MANI-APIGateway ]
       │
       ▼ (/api/v1/dispatch/* ──> puerto 5000)
[ MANI-Dispatch-DotNet ] ◄──(Llamadas internas REST)─── [ MANI-Node ]
```

### Relación con otros repositorios:
1. **`MANI-APIGateway`:** Redirige todas las peticiones con prefijo `/api/v1/dispatch/` hacia este servicio en el puerto `5000`.
2. **`MANI-Node`:** Invoca a este servicio cuando una solicitud de cliente pasa a estado de despacho.
3. **`MANI-Rules-Java`:** Puede ser consultado por este servicio para obtener el orden de prelación/ranking de los profesionales evaluados.
4. **`MANI-Flutter`:** Invoca las operaciones de solicitud de servicio y aceptación del aliado a través del Gateway.

---

## 3. Protocolos y Estándares
* **Endpoints:**
  - `GET /health` y `GET /api/v1/dispatch/health`: Healthcheck.
  - `POST /api/v1/dispatch/match`: Ejecuta el algoritmo de cálculo de proximidad geográfica.
  - `POST /api/v1/dispatch/requests/{requestId}/accept`: Aceptación atómica con exclusión concurrente (`200 OK` vs `409 Conflict`).
* **Encabezados Requeridos:**
  - `X-Correlation-ID`: Identificador de correlación para observabilidad distribuida.
  - `Authorization`: Token JWT para validaciones de identidad y tenant.

---

## 4. Comandos de Desarrollo
```bash
# Restaurar dependencias
dotnet restore

# Ejecutar localmente
dotnet run

# Ejecutar pruebas
dotnet test
```
