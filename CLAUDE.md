# CLAUDE.md — Guía de Desarrollo para MANI-Dispatch-DotNet

Este documento sintetiza las reglas de arquitectura y patrones de código para trabajar en el microservicio de Despacho y Emparejamiento de MANI.

---

## 1. Contexto de Arquitectura
* **Microservicio:** Despacho y Emparejamiento (.NET 8).
* **Consumidores:** Peticiones HTTP a través de **`MANI-APIGateway`** (`/api/v1/dispatch/*`) y llamadas internas de **`MANI-Node`**.
* **Principio Clave:** El servicio debe garantizar atomicidad estricta para resolver colisiones de aceptación en condiciones de alta concurrencia (*Race Conditions*).

---

## 2. Convenciones de Código
* **Estructura:**
  - `Controllers/`: Controladores API REST.
  - `Services/`: Algoritmo de cercanía y cálculo de tiempos estimados de llegada (ETA).
  - `Models/` o `DTOs/`: Records inmutables para request y response.
* **Exclusión Concurrente:** Usar operaciones atómicas condicionales (en base de datos o almacenamiento atómico en memoria/Redis) para evitar asignaciones dobles. Retornar siempre `409 Conflict` cuando una orden ya fue tomada.
* **Trazabilidad:** Leer y reflejar el encabezado `X-Correlation-ID`.
