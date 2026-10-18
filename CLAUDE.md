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

---

## Autoría y atribución (obligatorio)

**El entregable lo firma la persona que lo presenta.** No se añaden líneas de coautoría, firmas, pies ni emojis que atribuyan el trabajo a una herramienta de IA, ni en commits, Pull Requests, documentos, código o comentarios. Tampoco se firma un documento o un ADR como autor: ese campo lleva el nombre de la persona responsable.

Regla vigente: `MANI-Docs` → [`governance/POLITICAS_DEVOPS_HERRAMIENTAS.md`](https://github.com/Trama-AS/MANI-docs/blob/main/governance/POLITICAS_DEVOPS_HERRAMIENTAS.md) §18.7 y [`wiki/05-proceso/trabajo-con-ia.md`](https://github.com/Trama-AS/MANI-docs/blob/main/wiki/05-proceso/trabajo-con-ia.md).

Por qué, para no revertirla «mejorando»: la política §18.1 ya exige revisión humana de todo resultado, y esa revisión es la garantía; §18.5 dice que una respuesta de IA no es una decisión arquitectónica, así que atribuirle autoría la contradice. La trazabilidad del cambio la dan el identificador de Jira, la rama, el PR y el historial de Git, que sí dicen qué cambió, por qué y quién lo aprobó.

Lo que sí se registra es el **método de verificación** —el comando ejecutado y su resultado, el archivo comparado, la consulta hecha—, no la herramienta que ayudó a escribirlo.

Si encuentras una atribución de ese tipo en un archivo que ya estás editando, quítala.
