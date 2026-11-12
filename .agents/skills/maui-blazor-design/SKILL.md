---
name: maui-blazor-design
description: Generates .NET MAUI Blazor Hybrid Razor components from design tokens. Use when creating or modifying UI components.
---

When generating UI code for this project:
- Output Razor components (.razor) with @page directives
- Use CSS files for styling (no Tailwind, no inline styles)
- Reference design tokens from DESIGN.md (colors as CSS custom properties, fonts, spacing)
- Use MAUI Blazor Hybrid patterns (BlazorWebView, native controls where appropriate)
- Keep components small and composable
- Use .razor.css for component-scoped styles
