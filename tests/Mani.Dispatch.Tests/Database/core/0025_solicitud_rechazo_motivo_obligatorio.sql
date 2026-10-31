-- =====================================================================
-- Migración 0025 (Core Node) — Motivo obligatorio y detalle de «Otro» en el rechazo de ofertas (SCRUM-1617)
-- =====================================================================
-- Decisión del PO (27-oct-2026, BUG-DSP-01): el motivo con el que el aliado rechaza una oferta es obligatorio y se
-- elige de una lista corta; «Otro» lleva texto libre. Hasta ahora `solicitud_rechazo.motivo` admitía NULL
-- (`ck_solicitud_rechazo_motivo`, MANI-APIGateway 005) y no había dónde guardar el texto de «Otro».
--   * `detalle`: texto libre de «Otro», de 3 a 280 caracteres sin espacios extremos.
--   * `ck_solicitud_rechazo_motivo_presente`: toda fila nueva lleva motivo.
--   * `ck_solicitud_rechazo_detalle_otro`: «Otro» exige detalle y los demás motivos no lo llevan.
--   Las dos reglas se crean NOT VALID: se aplican a toda fila nueva o modificada, pero no a los rechazos previos,
--   que pudieron quedar con motivo NULL mientras era opcional. Nunca se inventa un motivo para esas filas (C3).
-- Dispatch (dominio dueño del rechazo) valida lo mismo antes de escribir y responde 422; estos CHECK impiden que
-- cualquier otro camino deje una fila que la API rechazaría. `mani_dispatch_svc` ya tiene SELECT/INSERT sobre la
-- tabla (0007); el GRANT es por tabla, así que cubre la columna nueva.
-- Aditiva e idempotente.

ALTER TABLE solicitud_rechazo ADD COLUMN IF NOT EXISTS detalle TEXT;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_solicitud_rechazo_detalle_longitud') THEN
        ALTER TABLE solicitud_rechazo ADD CONSTRAINT ck_solicitud_rechazo_detalle_longitud
            CHECK (detalle IS NULL OR (detalle = btrim(detalle) AND char_length(detalle) BETWEEN 3 AND 280));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_solicitud_rechazo_motivo_presente') THEN
        ALTER TABLE solicitud_rechazo ADD CONSTRAINT ck_solicitud_rechazo_motivo_presente
            CHECK (motivo IS NOT NULL) NOT VALID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_solicitud_rechazo_detalle_otro') THEN
        ALTER TABLE solicitud_rechazo ADD CONSTRAINT ck_solicitud_rechazo_detalle_otro
            CHECK ((motivo = 'OTRO') = (detalle IS NOT NULL)) NOT VALID;
    END IF;
END $$;

INSERT INTO schema_migrations (version, description)
VALUES ('0025', 'SCRUM-1617 solicitud_rechazo: motivo obligatorio en filas nuevas y detalle de OTRO')
ON CONFLICT (version) DO NOTHING;
