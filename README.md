# Sistema de Catálogo e Gerenciamento de Estoque de Roupas (.NET 10)

Sistema integrado para loja de roupas composto por um **Site Público (Catálogo Digital em Blazor Web App)**, um **Aplicativo Administrativo (.NET MAUI)** e um **Backend Unificado (ASP.NET Core Web API)** com banco de dados relacional **PostgreSQL (Supabase)**, armazenamento de imagens no **Supabase Storage** e containerização pronta para **Google Cloud Run**.

---

## 1. Estrutura da Solução (`appmaui.blazor.slnx`)

| Projeto | Caminho | Descrição |
| :--- | :--- | :--- |
| **ClothingStore.Domain** | [ClothingStore.Domain.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Domain/ClothingStore.Domain.csproj) | Entidades de domínio (`Role`, `User`, `Employee`, `Category`, `Product`, `ProductVariant`, `ProductImage`, `StockMovement`, `StoreSettings`) e regras de negócio. |
| **ClothingStore.Shared** | [ClothingStore.Shared.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Shared/ClothingStore.Shared.csproj) | DTOs, contratos de API, clientes HTTP tipados (`CatalogApiClient`, `AdminApiClient`) e gerador oficial de links WhatsApp (`WhatsAppLinkBuilder`). |
| **ClothingStore.Application** | [ClothingStore.Application.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Application/ClothingStore.Application.csproj) | Casos de uso e serviços de aplicação (`AuthService`, `CatalogService`, `ProductManagementService`, `StockManagementService`, `EmployeeAndSettingsService`). |
| **ClothingStore.Infrastructure** | [ClothingStore.Infrastructure.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj) | Repositório transacional sincronizado (`ClothingStoreRepository`), autenticação JWT (`JwtTokenService`), hash PBKDF2 (`Pbkdf2PasswordHasher`) e integração com **Supabase Storage** (`SupabaseImageStorageService`). |
| **ClothingStore.Api** | [ClothingStore.Api.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Api/ClothingStore.Api.csproj) | ASP.NET Core Web API com endpoints públicos e administrativos protegidos por JWT e Policies verificadas no backend. Inclui [Dockerfile](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Api/Dockerfile) para Google Cloud Run. |
| **ClothingStore.Web** | [ClothingStore.Web.csproj](file:///C:/Dev/appmaui.blazor/src/ClothingStore.Web/ClothingStore.Web.csproj) | Site público em Blazor Web App funcionando exclusivamente como catálogo digital com integração oficial via WhatsApp (`https://wa.me/NUMERO?text=MENSAGEM`). |
| **appmaui.blazor** | [appmaui.blazor.csproj](file:///C:/Dev/appmaui.blazor/appmaui.blazor.csproj) | Aplicativo `.NET MAUI` para o proprietário e funcionários gerenciarem dashboard, produtos, variantes, estoque, funcionários e **Configurações da loja**. |
| **ClothingStore.UnitTests** | [ClothingStore.UnitTests.csproj](file:///C:/Dev/appmaui.blazor/tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj) | Suíte de testes automatizados validando regras de negócio, movimentações de estoque, permissões e sincronização das configurações da loja. |

---

## 2. Credenciais Padrão para Acesso no Aplicativo (.NET MAUI)

- **Proprietária / Administradora (`Admin` — Acesso Completo + Configurações da Loja)**:
  - **E-mail**: `admin@loja.com`
  - **Senha**: `Admin@123`
- **Funcionário (`Employee` — Produtos e Estoque)**:
  - **E-mail**: `funcionario@loja.com`
  - **Senha**: `Func@123`

---

## 3. Como Executar Localmente

### 3.1. Executar a ASP.NET Core Web API (`http://localhost:5180`)
```powershell
dotnet run --project src/ClothingStore.Api/ClothingStore.Api.csproj
```

### 3.2. Executar o Site Público — Catálogo Blazor (`http://localhost:5190`)
```powershell
dotnet run --project src/ClothingStore.Web/ClothingStore.Web.csproj
```

### 3.3. Executar o Aplicativo Mobile/Desktop (.NET MAUI)
Abra a solução [appmaui.blazor.slnx](file:///C:/Dev/appmaui.blazor/appmaui.blazor.slnx) no Visual Studio e inicie o projeto `appmaui.blazor` (Windows Machine ou Emulador Android), ou via CLI:
```powershell
dotnet build appmaui.blazor.csproj -f net10.0-windows10.0.19041.0
```

### 3.4. Executar os Testes Automatizados
```powershell
dotnet build tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj -m:1
.\tests\ClothingStore.UnitTests\bin\Debug\net10.0\ClothingStore.UnitTests.exe
```

---

## 4. Banco de Dados Supabase e Deploy no Google Cloud Run

1. **Banco de Dados PostgreSQL (Supabase)**:
   Execute o script [supabase_schema.sql](file:///C:/Dev/appmaui.blazor/database/supabase_schema.sql) no **SQL Editor** do seu painel Supabase para criar as tabelas `roles`, `users`, `employees`, `categories`, `products`, `product_variants`, `product_images`, `stock_movements` e `store_settings` com todos os índices e constraints.
2. **Supabase Storage**:
   Crie um bucket público chamado `clothing-images` no Supabase Storage e configure `SUPABASE_URL` e `SUPABASE_SERVICE_ROLE_KEY` nas variáveis de ambiente da API (veja [.env.example](file:///C:/Dev/appmaui.blazor/.env.example)).
3. **Deploy da API no Google Cloud Run**:
   ```bash
   gcloud run deploy clothingstore-api \
     --source . \
     --dockerfile src/ClothingStore.Api/Dockerfile \
     --region southamerica-east1 \
     --allow-unauthenticated \
     --set-env-vars="JWT_SECRET_KEY=...,SUPABASE_URL=...,SUPABASE_SERVICE_ROLE_KEY=..."
   ```

