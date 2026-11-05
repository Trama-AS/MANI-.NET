Copia sin cambios de las migraciones de `Trama-AS/MANI-Node` (`db/migrations`) que tocan tablas del
dominio de Dispatch y que las pruebas de integración necesitan sobre el esquema oficial del Gateway
(`../migrations`). Se aplican después de esas, en orden.

- `0025_solicitud_rechazo_motivo_obligatorio.sql` (SCRUM-1617): columna `detalle` y `CHECK` del motivo
  obligatorio y del detalle de `OTRO` en `solicitud_rechazo`.

Si cambian en MANI-Node, hay que volver a copiarlas aquí.
