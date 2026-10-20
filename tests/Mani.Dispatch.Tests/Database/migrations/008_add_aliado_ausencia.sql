-- =====================================================================
-- 008 — Ausencias del aliado (US-02.1.6 / SCRUM-855)
--
-- El aliado se marca "No disponible" por intervalos (vacaciones, enfermedad)
-- y durante ese intervalo sale de la elegibilidad del despacho (SCRUM-861).
-- La API vive en Core Node (/profiles/me/unavailability, ADR-0028: la
-- disponibilidad es dominio de Core).
--
-- Decisión de modelo (Sprint 4): es una tabla de AUSENCIAS, no la agenda de
-- franjas positivas `core.disponibilidad` de MANI-docs/architecture/ModeloDatos.md.
-- Con ausencias el aliado es elegible por defecto y solo se excluye mientras
-- dura la ausencia. La ausencia es global al aliado (no depende de categoría
-- ni de zona). Los nombres siguen el esquema vivo (public, id, tenant_id,
-- aliado_id), no los de ModeloDatos.md.
--
-- Todo lo que garantiza la base es declarativo (ADR-0022, sin PL/pgSQL nuevo):
--   * inicio < fin                       ck_aliado_ausencia_rango
--   * sin solapamientos del mismo aliado ex_aliado_ausencia_solapamiento
--   * tenant_id igual al del aliado      fk_aliado_ausencia_aliado_tenant
--
-- Idempotente: se puede reaplicar sin error.
-- =====================================================================

-- EXCLUDE con igualdad sobre uuid necesita los operadores de btree_gist.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Destino de la FK compuesta (aliado_id, tenant_id). id ya es PK, así que la
-- restricción no cambia qué filas son válidas en aliado.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_aliado_id_tenant') THEN
        ALTER TABLE aliado ADD CONSTRAINT ux_aliado_id_tenant UNIQUE (id, tenant_id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS aliado_ausencia (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    aliado_id UUID NOT NULL,
    inicio TIMESTAMPTZ NOT NULL,
    fin TIMESTAMPTZ NOT NULL,
    motivo TEXT NOT NULL,
    nota TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT fk_aliado_ausencia_aliado_tenant FOREIGN KEY (aliado_id, tenant_id)
        REFERENCES aliado (id, tenant_id) ON DELETE CASCADE,
    CONSTRAINT ck_aliado_ausencia_rango CHECK (inicio < fin),
    CONSTRAINT ck_aliado_ausencia_motivo CHECK (motivo IN ('VACACIONES', 'ENFERMEDAD', 'OTRO')),
    CONSTRAINT ck_aliado_ausencia_nota CHECK (nota IS NULL OR char_length(nota) <= 280),
    -- Intervalo semiabierto [inicio, fin): una ausencia que termina a las 12:00
    -- y otra que empieza a las 12:00 no se solapan.
    CONSTRAINT ex_aliado_ausencia_solapamiento EXCLUDE USING gist (
        aliado_id WITH =,
        tstzrange(inicio, fin, '[)') WITH &&
    )
);

-- La elegibilidad (SCRUM-861) pregunta "¿tiene el aliado una ausencia que
-- cubra este instante?": filtra por tenant, aliado y rango.
CREATE INDEX IF NOT EXISTS idx_aliado_ausencia_tenant_aliado
    ON aliado_ausencia (tenant_id, aliado_id, inicio, fin);

-- RLS: Core Node escribe con la service role (que salta RLS) y valida rol y
-- tenant en el caso de uso. Con el JWT del usuario, el aliado solo LEE sus
-- propias ausencias de su tenant; sin políticas de escritura, solo Core escribe.
ALTER TABLE aliado_ausencia ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    -- Solo en Supabase (con auth.uid y auth.jwt). Se consulta pg_namespace en vez
    -- de 'auth'::regnamespace porque el cast falla si el esquema auth no existe.
    IF (SELECT count(DISTINCT p.proname) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'auth' AND p.proname IN ('uid', 'jwt')) = 2 THEN
        DROP POLICY IF EXISTS "Aliado lee sus propias ausencias" ON aliado_ausencia;
        CREATE POLICY "Aliado lee sus propias ausencias" ON aliado_ausencia
        FOR SELECT USING (
            tenant_id::text = (auth.jwt() -> 'app_metadata' ->> 'tenant_id')
            AND aliado_id IN (SELECT id FROM aliado WHERE usuario_id = auth.uid())
        );
    END IF;
END $$;

INSERT INTO schema_migrations (version, description)
VALUES ('008', 'SCRUM-855 agrega aliado_ausencia: intervalos de no disponibilidad del aliado')
ON CONFLICT (version) DO NOTHING;
