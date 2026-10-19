Copia sin cambios de las migraciones 001-008 de `Trama-AS/MANI-APIGateway`
(`database/migrations`, rama `develop`). Las pruebas de integración las aplican, en orden,
sobre un PostgreSQL 15 real (Testcontainers) para que las consultas de Dispatch se
verifiquen contra el esquema oficial y no contra uno inventado.

Si cambian las migraciones oficiales, hay que volver a copiarlas aquí.
