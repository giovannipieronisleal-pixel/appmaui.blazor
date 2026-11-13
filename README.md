# YouLink — Catálogo Digital & Painel da Loja (.NET 10)

**YouLink** é uma plataforma para lojas de roupas composta por um **Aplicativo Mobile/Desktop (.NET MAUI Blazor Hybrid)**, um **Site Público (Catálogo Digital em Blazor Web App)** e uma **Web API (ASP.NET Core)** com integração ao WhatsApp, persistência compatível com **PostgreSQL (Supabase)**, imagens no **Supabase Storage** e script pronto para **Google Cloud Run**.

---

## Baixar o App Pronto (GitHub Releases / Actions)

Os instaladores prontos (**Android `.apk`** e **Windows `win-x64 .zip`**) são gerados automaticamente pelo workflow [release-youlink.yml](.github/workflows/release-youlink.yml):

1. Acesse a aba **Releases** do repositório para baixar a versão empacotada (`YouLink-Android.apk` e `YouLink-Windows-x64.zip`).
2. Ou acesse a aba **Actions** → último workflow executado com sucesso → baixe os **Artifacts** da compilação mais recente.

> **Nota para Android:** por ser uma compilação distribuída diretamente fora da Play Store, ative *"Instalar apps desconhecidos"* ao abrir o arquivo `.apk` no celular.

---

## Planos & Identidade Visual da Vitrine

O **YouLink** conta com três níveis de vitrine selecionáveis no app (`/planos` e `/configuracoes-loja`), **todos já com Servidor 24h + Certificado HTTPS + Endereço/Domínio da loja inclusos sem custo adicional**:

- **Plano Básico (`Basico`)**: tema fixo *Minimalista Luxo* + servidor 24h + HTTPS + endereço da vitrine inclusos, pronto para vender via WhatsApp, com QR Code e estoque em tempo real.
- **Meia Versão (`Intermediario`)**: **5 versões de site pré-prontas** não configuráveis para troca instantânea em 1 clique + servidor 24h + HTTPS + domínio/endereço incluso.
- **Premium Sob Medida (`Premium`)**: Estúdio Visual completo (paleta, fontes, cantos, densidade, destaque e grade configurável) + servidor 24h + HTTPS + endereço incluso (com opção de domínio próprio sem taxa extra da plataforma).

---

## Estrutura Pública da Solução (`appmaui.blazor.slnx`)

| Projeto | Caminho | Descrição |
| :--- | :--- | :--- |
| **YouLink (`appmaui.blazor`)** | [appmaui.blazor.csproj](appmaui.blazor.csproj) | Aplicativo `.NET MAUI Blazor Hybrid` (Android, Windows, iOS, MacCatalyst) para gerenciar catálogo, estoque, equipe, planos e visual da loja. |
| **ClothingStore.Web** | [ClothingStore.Web.csproj](src/ClothingStore.Web/ClothingStore.Web.csproj) | Site público em Blazor Web App funcionando como catálogo digital com integração ao WhatsApp (`https://wa.me/NUMERO?text=MENSAGEM`). |
| **ClothingStore.Api** | [ClothingStore.Api.csproj](src/ClothingStore.Api/ClothingStore.Api.csproj) | ASP.NET Core Web API com endpoints públicos e administrativos protegidos por JWT e Policies no backend. Inclui [Dockerfile](src/ClothingStore.Api/Dockerfile). |
| **ClothingStore.Application** | [ClothingStore.Application.csproj](src/ClothingStore.Application/ClothingStore.Application.csproj) | Regras de aplicação (`AuthService`, `CatalogService`, `ProductManagementService`, `StockManagementService`, `EmployeeAndSettingsService`). |
| **ClothingStore.Domain** | [ClothingStore.Domain.csproj](src/ClothingStore.Domain/ClothingStore.Domain.csproj) | Entidades de domínio (`Role`, `User`, `Employee`, `Category`, `Product`, `ProductVariant`, `ProductImage`, `StockMovement`, `StoreSettings`, `PlanActivationCode`). |
| **ClothingStore.Infrastructure** | [ClothingStore.Infrastructure.csproj](src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj) | Repositório transacional (`ClothingStoreRepository`), autenticação JWT/PBKDF2 e armazenamento de imagens. |
| **ClothingStore.Shared** | [ClothingStore.Shared.csproj](src/ClothingStore.Shared/ClothingStore.Shared.csproj) | DTOs, clientes HTTP (`CatalogApiClient`, `AdminApiClient`), design tokens e helper de links/domínios. |
| **ClothingStore.UnitTests** | [ClothingStore.UnitTests.csproj](tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj) | Suíte com 45 testes automatizados de domínio, segurança, planos, domínios e stress tests. |

---

## Como Compilar e Executar Localmente

### 1. API (`http://localhost:5180`)
```powershell
dotnet run --project src/ClothingStore.Api/ClothingStore.Api.csproj
```

### 2. Site Público — Catálogo Web (`http://localhost:5190`)
```powershell
dotnet run --project src/ClothingStore.Web/ClothingStore.Web.csproj
```

### 3. Aplicativo YouLink (.NET MAUI)
```powershell
# Windows
dotnet build appmaui.blazor.csproj -f net10.0-windows10.0.19041.0 -c Release

# Android (APK)
dotnet publish appmaui.blazor.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

### 4. Testes Automatizados
```powershell
dotnet build tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj -m:1
.\tests\ClothingStore.UnitTests\bin\Debug\net10.0\ClothingStore.UnitTests.exe
```

---

## Banco Supabase & Publicação na Nuvem (Camadas Gratuitas Inclusas)

1. Execute [supabase_schema.sql](database/supabase_schema.sql) no **SQL Editor** do Supabase (Free Tier).
2. Configure o bucket `clothing-images` e as variáveis de ambiente seguindo [.env.example](.env.example).
3. Publique uma única instância compartilhada para atender todas as lojas via `/loja/{slug}` em hospedagem gratuita (Render `.onrender.com`, Koyeb `.koyeb.app`, Fly.io `.fly.dev`) ou Google Cloud Run ([deploy-google-cloud.ps1](deploy-google-cloud.ps1)), mantendo servidor e endereço já inclusos dentro do plano sem encarecer para o usuário.
