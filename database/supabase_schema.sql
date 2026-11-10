-- =============================================================================
-- SISTEMA DE CATÁLOGO E GERENCIAMENTO DE ESTOQUE DE ROUPAS
-- Script DDL + Seed Inicial para PostgreSQL (Supabase)
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- 1. ROLES (Perfis de Acesso)
CREATE TABLE IF NOT EXISTS public.roles (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(50) NOT NULL UNIQUE,
    description varchar(200) NULL,
    default_permissions jsonb NOT NULL DEFAULT '{}'::jsonb
);

-- 2. USERS (Contas de Autenticação com proteção contra força bruta e entrada por código no e-mail)
CREATE TABLE IF NOT EXISTS public.users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email varchar(150) NOT NULL UNIQUE,
    password_hash text NOT NULL,
    role_id uuid NOT NULL REFERENCES public.roles(id) ON DELETE RESTRICT,
    is_active boolean NOT NULL DEFAULT true,
    reset_token_hash text NULL,
    reset_token_expires_at timestamptz NULL,
    email_login_code_hash text NULL,
    email_login_code_expires_at timestamptz NULL,
    failed_login_attempts int NOT NULL DEFAULT 0,
    lockout_end_at timestamptz NULL,
    last_login_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email_lower ON public.users (LOWER(email));

-- 3. EMPLOYEES (Funcionários e Permissões Operacionais)
CREATE TABLE IF NOT EXISTS public.employees (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL UNIQUE REFERENCES public.users(id) ON DELETE CASCADE,
    full_name varchar(120) NOT NULL,
    phone varchar(30) NULL,
    job_title varchar(80) NULL,
    can_manage_products boolean NOT NULL DEFAULT false,
    can_manage_stock boolean NOT NULL DEFAULT true,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- 4. CATEGORIES (Categorias de Roupas)
CREATE TABLE IF NOT EXISTS public.categories (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(80) NOT NULL,
    slug varchar(100) NOT NULL UNIQUE,
    description varchar(300) NULL,
    display_order int NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT true
);

-- 5. PRODUCTS (Produtos do Catálogo)
CREATE TABLE IF NOT EXISTS public.products (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    category_id uuid NOT NULL REFERENCES public.categories(id) ON DELETE RESTRICT,
    name varchar(150) NOT NULL,
    slug varchar(180) NOT NULL UNIQUE,
    description text NULL,
    base_price numeric(10,2) NOT NULL CHECK (base_price >= 0),
    is_visible_on_site boolean NOT NULL DEFAULT true,
    is_archived boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_products_catalog_filter
    ON public.products (is_visible_on_site, is_archived, category_id);

-- 6. PRODUCT_VARIANTS (Combinações de Cor + Tamanho com Estoque Independente)
CREATE TABLE IF NOT EXISTS public.product_variants (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id uuid NOT NULL REFERENCES public.products(id) ON DELETE CASCADE,
    sku varchar(60) NOT NULL UNIQUE,
    color varchar(50) NOT NULL,
    color_hex varchar(7) NULL,
    size varchar(20) NOT NULL,
    stock_quantity int NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
    low_stock_threshold int NOT NULL DEFAULT 3 CHECK (low_stock_threshold >= 0),
    is_active boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_product_variants_product_color_size
    ON public.product_variants (product_id, LOWER(color), UPPER(size))
    WHERE is_active = true;

CREATE INDEX IF NOT EXISTS ix_product_variants_lookup
    ON public.product_variants (product_id, color, size, stock_quantity);

-- 7. PRODUCT_IMAGES (Fotografias dos Produtos no Supabase Storage)
CREATE TABLE IF NOT EXISTS public.product_images (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id uuid NOT NULL REFERENCES public.products(id) ON DELETE CASCADE,
    storage_path text NOT NULL,
    public_url text NOT NULL,
    alt_text varchar(150) NULL,
    display_order int NOT NULL DEFAULT 0,
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_product_images_product_order
    ON public.product_images (product_id, is_primary DESC, display_order ASC);

-- 8. STOCK_MOVEMENTS (Histórico Auditável de Movimentações de Estoque)
CREATE TABLE IF NOT EXISTS public.stock_movements (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_variant_id uuid NOT NULL REFERENCES public.product_variants(id) ON DELETE RESTRICT,
    employee_id uuid NOT NULL REFERENCES public.employees(id) ON DELETE RESTRICT,
    movement_type varchar(20) NOT NULL CHECK (movement_type IN ('In', 'Out', 'Adjustment', 'IN', 'OUT', 'ADJUSTMENT')),
    quantity_changed int NOT NULL CHECK (quantity_changed <> 0),
    previous_quantity int NOT NULL CHECK (previous_quantity >= 0),
    new_quantity int NOT NULL CHECK (new_quantity >= 0),
    reason varchar(250) NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_stock_movements_created_at
    ON public.stock_movements (created_at DESC);

CREATE INDEX IF NOT EXISTS ix_stock_movements_variant
    ON public.stock_movements (product_variant_id, created_at DESC);

-- 9. STORE_SETTINGS (Configurações Personalizadas da Loja - Preparado para expansão Multi-Loja via store_slug)
CREATE TABLE IF NOT EXISTS public.store_settings (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_name varchar(120) NOT NULL,
    store_slug varchar(140) NOT NULL UNIQUE DEFAULT 'minha-loja',
    whatsapp_number varchar(20) NOT NULL,
    store_description text NOT NULL,
    logo_storage_path text NULL,
    logo_public_url text NULL,
    default_low_stock_threshold int NOT NULL DEFAULT 3 CHECK (default_low_stock_threshold >= 0),
    online_base_url text NULL,
    auto_sync_online_on_save boolean NOT NULL DEFAULT true,
    last_online_sync_at timestamptz NULL,
    updated_by_employee_id uuid NULL REFERENCES public.employees(id) ON DELETE SET NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- =============================================================================
-- SEED INICIAL DE PERFIS E CONFIGURAÇÕES
-- =============================================================================
INSERT INTO public.roles (id, name, description, default_permissions)
VALUES
    ('11111111-1111-1111-1111-111111111111', 'Admin', 'Acesso total ao sistema, funcionários e configurações da loja', '{"all": true}'::jsonb),
    ('22222222-2222-2222-2222-222222222222', 'Employee', 'Funcionário com permissões operacionais de produtos e estoque', '{"stock": true}'::jsonb)
ON CONFLICT (name) DO NOTHING;

