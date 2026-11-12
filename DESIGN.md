# DESIGN.md — Minimalista Luxo & Estúdio Visual Sob Medida

> **Sistema Visual Oficial**: Minimalista Luxo — Catálogo Digital & Painel da Loja

---

## 1. Philosophy & Tier Architecture

This project serves **three public storefront plan tiers** selectable from `/planos` (`PlansPage.razor`) and managed inside `/configuracoes-loja` (`StoreSettingsPage.razor`):

1. **Plano Básico (`Basico`)**:
   - Delivers the curated **Minimalista Luxo** design system out of the box (Alabaster Silk `#FAF8F5`, Obsidian Ink `#18181B`, Champagne Gold `#B69158`, `Playfair Display` + `Plus Jakarta Sans`).
   - Ensures effortless visual polish with locked design controls on `/configuracoes-loja` (showing an upgrade preview).
2. **Plano Intermediário — Meia Versão (`Intermediario`)**:
   - Unlocks **5 pre-ready, non-configurable storefront versions** (`minimalista-luxo`, `editorial-vogue`, `boutique-rose`, `urban-sartorial`, `noir-noturno`) switchable directly on `/planos` or `/configuracoes-loja`.
3. **Plano Premium — Estúdio Visual Sob Medida (`Premium`)**:
   - Unlocks live, store-specific design molding in `/configuracoes-loja` (`DesignStudioPanel.razor`).
   - Store owners can customize **presets**, **7 semantic colors**, **headline & body typography**, **corner radius**, **layout density**, **hero composition**, **grid columns (2/3/4)**, and **custom editorial copy** (`HeroEyebrow`, `HeroHeadline`).
   - Persists in `StoreSettings.DesignConfig` and propagates simultaneously to:
     - Public Blazor Web storefront (`ClothingStore.Web`)
     - MAUI Blazor Hybrid admin app (`appmaui.blazor`) and in-app preview (`PublicCatalogPreview.razor`)
     - Embedded local HTTP catalog (`LocalWebServerLauncher.cs`)

---

## 2. Default Palette — Minimalista Luxo (`LIGHT`)

| Role | Token | Default Hex | Description |
| :--- | :--- | :--- | :--- |
| Canvas Background | `--brand-bg` | `#FAF8F5` | Warm Alabaster Silk page backdrop |
| Elevated Card Surface | `--brand-surface` | `#FFFFFF` | Pure Silk cards, modals, inputs |
| Subtle Tinted Surface | `--brand-surface-subtle` | `#F3EFE8` | Cashmere Cream badges, table headers, swatches |
| Primary Ink / Action | `--brand-primary` | `#18181B` | Obsidian Charcoal buttons and headings |
| Primary Button Contrast | `--brand-primary-fg` | `#FAF8F5` | Alabaster text over Obsidian |
| Luxury Accent | `--brand-accent` | `#B69158` | Champagne Gold eyebrows, active indicators |
| Body Text | `--brand-text` | `#18181B` | High-contrast editorial ink |
| Secondary / Muted Text | `--brand-text-muted` | `#57534E` | Warm Stone metadata and subtitles |
| Hairline Border | `--brand-border` | `#E7E2DA` | Delicate 1px sand divider |
| Admin Sidebar Surface | `--brand-sidebar` | `#141416` | Deep Obsidian navigation rail |
| WhatsApp Action | `--brand-whatsapp` | `#16A34A` | Direct boutique concierge CTA (`#15803D` hover) |

### Curated Premium Presets
- **Minimalista Luxo** (`minimalista-luxo`): `#FAF8F5` / `#FFFFFF` / `#18181B` / `#B69158` (`Playfair Display` + `Plus Jakarta Sans`, `ROUND_FOUR`)
- **Editorial Vogue** (`editorial-vogue`): `#F7F5F2` / `#FFFFFF` / `#0F0F10` / `#9A7B4F` (`Bodoni Moda` + `Inter`, square `SHARP`)
- **Boutique Rosé** (`boutique-rose`): `#FDF8F6` / `#FFFFFF` / `#3D2329` / `#C4847A` (`Cormorant Garamond` + `DM Sans`, `ROUND_TWELVE`)
- **Urban Sartorial** (`urban-sartorial`): `#F4F4F5` / `#FFFFFF` / `#09090B` / `#52525B` (`Space Grotesk` + `Inter`, `ROUND_FOUR`)
- **Noir Noturno** (`noir-noturno`): `#0E0E10` / `#18181B` / `#E5C99B` / `#C8A267` (`Playfair Display` + `Plus Jakarta Sans`, dark luxury canvas)

---

## 3. Typography Scale

- **Display / Headline Font (`--brand-font-display`)**:
  - Default: `'Playfair Display', Georgia, 'Times New Roman', serif`
  - Selectable (Premium): `Playfair Display`, `Cormorant Garamond`, `Bodoni Moda`, `Montserrat`, `Space Grotesk`
- **Body / UI Font (`--brand-font-body`)**:
  - Default: `'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif`
  - Selectable (Premium): `Plus Jakarta Sans`, `Inter`, `DM Sans`, `Montserrat`, `Work Sans`

| Style | Size | Weight | Tracking / Leading | Usage |
| :--- | :--- | :--- | :--- | :--- |
| Hero Title | `clamp(2.25rem, 4.4vw, 3.6rem)` | `500–600` | `-0.025em` / `1.08` | Storefront editorial display heading |
| Eyebrow Label | `0.72rem` | `600` | `+0.22em` uppercase | Section & collection kickers in `--brand-accent` |
| Section Heading | `clamp(1.5rem, 2.5vw, 2.1rem)` | `500` | `-0.02em` / `1.2` | Featured collections, Admin page titles |
| Card Title | `1.05rem` | `600` | `-0.01em` / `1.35` | Product titles, KPI panels |
| Body / Form Label | `0.94rem` | `400–500` | `0` / `1.6` | Descriptions, form fields |
| Caption / Badge | `0.75rem` | `600` | `+0.08em` | Stock pills, color chips, category tags |

---

## 4. Shape, Radius & Spacing Contract

Generated from `StoreDesignDto.CornerRadiusMode` (`--brand-radius-*`) and `StoreDesignDto.DensityMode`:

- **Corner Radius Modes (`Roundness`)**:
  - `SHARP` (`Reto Editorial`): `--brand-radius-card: 2px; --brand-radius-btn: 2px; --brand-radius-pill: 4px;`
  - `ROUND_FOUR` (`Suave Luxo` — default): `--brand-radius-card: 8px; --brand-radius-btn: 6px; --brand-radius-pill: 999px;`
  - `ROUND_TWELVE` (`Arredondado Atelier`): `--brand-radius-card: 16px; --brand-radius-btn: 12px; --brand-radius-pill: 999px;`
- **Layout Density Modes**:
  - `compact`: tighter card padding (`0.95rem`) and grid gap (`1rem`)
  - `balanced` (default): balanced editorial spacing (`1.25rem` padding, `1.5rem` gap)
  - `airy`: generous runway whitespace (`1.75rem` padding, `2.1rem` gap)
- **Hero Layouts (Premium customizable)**:
  - `split` (`Split Editorial`): 2-column editorial title + 3-step concierge card / lookbook showcase
  - `centered` (`Minimal Center`): Centered gallery-style headline and CTA stack
  - `banner` (`Banner Imersivo`): Full-width dark-contrast editorial statement banner
- **Product Grid Columns (Premium customizable)**:
  - `2`, `3` (default), or `4` columns on desktop (collapsing gracefully on tablet/mobile)

---

## 5. Component Rules (per `.agents/skills/maui-blazor-design/SKILL.md`)

1. All Razor UI components live in `.razor` + scoped `.razor.css` (or global `app.css` theme tokens).
2. Avoid Tailwind CDN dependencies in Blazor Razor apps; use semantic CSS classes powered by `--brand-*` / `--adm-*` variables.
3. Root wrappers bind only the computed dynamic CSS variable string from `StoreDesignCssHelper` (`ClothingStore.Shared`) so `Basico` always resolves cleanly to Minimalista Luxo while `Premium` reflects client edits instantaneously.
4. Preserve MAUI `ThemeService` Light/Dark/System override inside the admin shell so dark-mode preference remains ergonomic during long inventory sessions.
