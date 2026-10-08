-- =====================================================================
-- Esquema DDL y Seed para MANI-.NET (Dispatch Service)
-- Proyecto Supabase: bxhvjzgjdgqsafaxikry
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- 1. Tablas Base
CREATE TABLE IF NOT EXISTS tenant (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    nombre TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    estado TEXT NOT NULL DEFAULT 'ACTIVO',
    fecha_alta TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS zona (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    nivel TEXT NOT NULL DEFAULT 'LOCALIDAD',
    nombre TEXT NOT NULL,
    zona_padre_id UUID REFERENCES zona(id) ON DELETE SET NULL,
    estado TEXT NOT NULL DEFAULT 'ACTIVO'
);

CREATE TABLE IF NOT EXISTS usuario (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID REFERENCES tenant(id) ON DELETE CASCADE,
    email TEXT NOT NULL,
    rol TEXT NOT NULL,
    estado TEXT NOT NULL DEFAULT 'ACTIVO',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS categoria_servicio (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    nombre TEXT NOT NULL,
    estado TEXT NOT NULL DEFAULT 'ACTIVO',
    flujo_operativo TEXT
);

CREATE TABLE IF NOT EXISTS aliado (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    usuario_id UUID NOT NULL UNIQUE REFERENCES usuario(id) ON DELETE CASCADE,
    tipo TEXT NOT NULL DEFAULT 'PERSONA_NATURAL',
    nombre_razon_social TEXT NOT NULL,
    estado_verificacion TEXT NOT NULL DEFAULT 'VERIFICADO',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS cliente (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    usuario_id UUID NOT NULL UNIQUE REFERENCES usuario(id) ON DELETE CASCADE,
    tipo TEXT NOT NULL DEFAULT 'PERSONA_NATURAL'
);

CREATE TABLE IF NOT EXISTS sitio (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    cliente_id UUID NOT NULL REFERENCES cliente(id) ON DELETE CASCADE,
    zona_id UUID NOT NULL REFERENCES zona(id) ON DELETE RESTRICT,
    nombre TEXT NOT NULL DEFAULT 'Sede Principal',
    direccion TEXT NOT NULL,
    ciudad TEXT NOT NULL DEFAULT 'Bogotá',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS aliado_categoria (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    aliado_id UUID NOT NULL REFERENCES aliado(id) ON DELETE CASCADE,
    categoria_id UUID NOT NULL REFERENCES categoria_servicio(id) ON DELETE CASCADE,
    CONSTRAINT uq_aliado_categoria UNIQUE (aliado_id, categoria_id)
);

CREATE TABLE IF NOT EXISTS cobertura_aliado (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    aliado_id UUID NOT NULL REFERENCES aliado(id) ON DELETE CASCADE,
    zona_id UUID NOT NULL REFERENCES zona(id) ON DELETE CASCADE,
    CONSTRAINT uq_aliado_cobertura UNIQUE (aliado_id, zona_id)
);

CREATE TABLE IF NOT EXISTS solicitud (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    cliente_id UUID REFERENCES cliente(id) ON DELETE SET NULL,
    sitio_id UUID REFERENCES sitio(id) ON DELETE SET NULL,
    categoria_id UUID NOT NULL REFERENCES categoria_servicio(id) ON DELETE RESTRICT,
    zona_id UUID NOT NULL REFERENCES zona(id) ON DELETE RESTRICT,
    aliado_id UUID REFERENCES aliado(id) ON DELETE SET NULL,
    estado TEXT NOT NULL DEFAULT 'PENDIENTE',
    descripcion TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS solicitud_rechazo (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenant(id) ON DELETE CASCADE,
    solicitud_id UUID NOT NULL REFERENCES solicitud(id) ON DELETE CASCADE,
    aliado_id UUID NOT NULL REFERENCES aliado(id) ON DELETE CASCADE,
    motivo TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ux_solicitud_rechazo UNIQUE (solicitud_id, aliado_id)
);

-- Índices de Desempeño para Despacho y Exclusión Concurrente (H-02 / ADR-0011)
CREATE INDEX IF NOT EXISTS idx_cobertura_aliado_tenant_zona
    ON cobertura_aliado (tenant_id, zona_id) INCLUDE (aliado_id);

CREATE INDEX IF NOT EXISTS idx_aliado_categoria_tenant_categoria
    ON aliado_categoria (tenant_id, categoria_id) INCLUDE (aliado_id);

CREATE INDEX IF NOT EXISTS idx_solicitud_tenant_estado
    ON solicitud (tenant_id, estado, created_at);

-- =====================================================================
-- SEED DE DATOS PARA PRUEBAS DE DESPACHO Y CONCURRENCIA
-- =====================================================================

DO $$
DECLARE
    v_tenant_id UUID := '10000000-0000-4000-8000-000000000011';
    v_zona_id UUID := '8a1b2c3d-0000-0000-0000-000000000001';
    v_categoria_id UUID := '9b2c3d4e-0000-0000-0000-000000000002';
    v_usr_aliado_1 UUID := 'a1111111-0000-0000-0000-000000000001';
    v_usr_aliado_2 UUID := 'a2222222-0000-0000-0000-000000000002';
    v_aliado_1 UUID := 'b1111111-0000-0000-0000-000000000001';
    v_aliado_2 UUID := 'b2222222-0000-0000-0000-000000000002';
    v_usr_cliente UUID := 'c1111111-0000-0000-0000-000000000001';
    v_cliente UUID := 'c2222222-0000-0000-0000-000000000001';
    v_sitio UUID := 'd1111111-0000-0000-0000-000000000001';
    v_solicitud_1 UUID := '3fa85f64-5717-4562-b3fc-2c963f66afa6';
BEGIN
    -- 1. Tenant
    INSERT INTO tenant (id, nombre, slug, estado)
    VALUES (v_tenant_id, 'ACME Servicios', 'acme-servicios', 'ACTIVO')
    ON CONFLICT (id) DO NOTHING;

    -- 2. Zona y Categoría
    INSERT INTO zona (id, nivel, nombre, estado)
    VALUES (v_zona_id, 'LOCALIDAD', 'Chapinero', 'ACTIVO')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO categoria_servicio (id, tenant_id, nombre, estado)
    VALUES (v_categoria_id, v_tenant_id, 'Plomería y Cerrajería', 'ACTIVO')
    ON CONFLICT (id) DO NOTHING;

    -- 3. Usuarios y Aliados
    INSERT INTO usuario (id, tenant_id, email, rol, estado)
    VALUES (v_usr_aliado_1, v_tenant_id, 'aliado1@acme.test', 'ALIADO', 'ACTIVE')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO usuario (id, tenant_id, email, rol, estado)
    VALUES (v_usr_aliado_2, v_tenant_id, 'aliado2@acme.test', 'ALIADO', 'ACTIVE')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO aliado (id, tenant_id, usuario_id, tipo, nombre_razon_social, estado_verificacion)
    VALUES (v_aliado_1, v_tenant_id, v_usr_aliado_1, 'PERSONA_NATURAL', 'Carlos Plomero', 'VERIFICADO')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO aliado (id, tenant_id, usuario_id, tipo, nombre_razon_social, estado_verificacion)
    VALUES (v_aliado_2, v_tenant_id, v_usr_aliado_2, 'PERSONA_NATURAL', 'Juan Cerrajero', 'VERIFICADO')
    ON CONFLICT (id) DO NOTHING;

    -- 4. Cobertura y Categoría de los Aliados
    INSERT INTO aliado_categoria (tenant_id, aliado_id, categoria_id)
    VALUES (v_tenant_id, v_aliado_1, v_categoria_id)
    ON CONFLICT DO NOTHING;

    INSERT INTO aliado_categoria (tenant_id, aliado_id, categoria_id)
    VALUES (v_tenant_id, v_aliado_2, v_categoria_id)
    ON CONFLICT DO NOTHING;

    INSERT INTO cobertura_aliado (tenant_id, aliado_id, zona_id)
    VALUES (v_tenant_id, v_aliado_1, v_zona_id)
    ON CONFLICT DO NOTHING;

    INSERT INTO cobertura_aliado (tenant_id, aliado_id, zona_id)
    VALUES (v_tenant_id, v_aliado_2, v_zona_id)
    ON CONFLICT DO NOTHING;

    -- 5. Cliente y Sitio
    INSERT INTO usuario (id, tenant_id, email, rol, estado)
    VALUES (v_usr_cliente, v_tenant_id, 'cliente1@acme.test', 'CLIENTE', 'ACTIVE')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO cliente (id, tenant_id, usuario_id, tipo)
    VALUES (v_cliente, v_tenant_id, v_usr_cliente, 'PERSONA_NATURAL')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO sitio (id, tenant_id, cliente_id, zona_id, nombre, direccion, ciudad)
    VALUES (v_sitio, v_tenant_id, v_cliente, v_zona_id, 'Apartamento Chapinero', 'Calle 67 # 9-20', 'Bogotá')
    ON CONFLICT (id) DO NOTHING;

    -- 6. Solicitud Pendiente para Despacho y Aceptación
    INSERT INTO solicitud (id, tenant_id, cliente_id, sitio_id, categoria_id, zona_id, estado, descripcion)
    VALUES (v_solicitud_1, v_tenant_id, v_cliente, v_sitio, v_categoria_id, v_zona_id, 'PENDIENTE', 'Reparación de tubería cocina')
    ON CONFLICT (id) DO NOTHING;
END $$;
