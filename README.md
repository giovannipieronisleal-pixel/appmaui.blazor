# YouLink — Catálogo Digital, Loja Online & Gestão (.NET 10)

O **YouLink** é um aplicativo para **Android e Windows** integrado a uma **vitrine web pública (`ClothingStore.Web`)** para lojas de roupas e varejo venderem pelo **WhatsApp** e **Pix**, com controle de estoque por variante, QR Code automático e publicação gratuita do site 24h em poucos cliques.

---

## Baixar o App Pronto (Windows e Android)

- 👉 **[Baixar a Última Versão (Latest Release)](https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest)** (`YouLink-Windows-x64.zip` e `YouLink-Android.apk`)
- 👉 **[Ver Aba Releases](https://github.com/giovannipieronisleal-pixel/yourlink/releases)** | **[GitHub Actions (Builds)](https://github.com/giovannipieronisleal-pixel/yourlink/actions)**

### Instalação e Atualização Direta no App
- **Windows (`YouLink-Windows-x64.zip`):** extraia a pasta e abra o executável (pacote *Self-Contained* x64 — não exige .NET pré-instalado).
- **Android (`YouLink-Android.apk`):** ao abrir o `.apk` fora da Play Store, ative *"Instalar apps desconhecidos"*.
- **Atualização automática integrada:** ao abrir o aplicativo, o YouLink verifica automaticamente o último release publicado e exibe um popup para baixar e instalar a nova versão dentro do próprio app (preservando os dados em `App_Data` no Windows).

---

## Publicar Seu Site Grátis 24h em 3 Passos (Direto no App)

Em **Loja & Visual**, qualquer lojista consegue colocar o catálogo no ar sem usar terminal nem código:

1. **Criar servidor gratuito 24h:** clique em **🌐 Criar Meu Site Grátis 24h** para abrir direto o Blueprint pronto no Render (`https://dashboard.render.com/blueprint/new?repo=https://github.com/giovannipieronisleal-pixel/yourlink`).
2. **Colar link e chave:** use os botões **📋 Colar** do assistente para informar a URL gerada (`https://...onrender.com`) e a chave `YOULINK_SYNC_SECRET` do painel do Render.
3. **Ativar em 1 clique:** clique em **Testar, Sincronizar e Ativar Link**. O aplicativo valida a conexão, envia produtos e visual, atualiza automaticamente o **Link Oficial**, o link do **WhatsApp** e o **QR Code** da loja, e mantém a vitrine re-sincronizada caso o container gratuito reinicie.

---

## Principais Recursos

### Gestão de Loja, Categorias & Estoque
- **Início limpo sem produtos de exemplo:** crie suas próprias categorias e cadastre produtos com fotos, cores, tamanhos e SKU.
- **Movimentação rápida em 1 toque:** botões **−1 / +1 / +5 / +10** por variante, filtros por peça/saldo baixo e histórico auditável de entradas e saídas.
- **QR Code & WhatsApp prontos:** link direto `https://wa.me/...` e QR Code gerados automaticamente para compartilhar a vitrine com os clientes.

### Planos, Estúdio Visual & Vendas via Pix
Todos os níveis incluem endereço da loja, HTTPS e integração com servidor web 24h:

- **Plano Básico (`Basico`):** visual *Minimalista Luxo* + vitrine pública + pedidos direto no WhatsApp.
- **Meia Versão (`Intermediario`):** **5 versões de site pré-prontas**, painel financeiro **Vendas & Quanto Vendeu** (`/vendas`) e **vendas diretas pelo app/site via Pix** (sem necessidade de cartão ou CNPJ).
- **Premium Sob Medida (`Premium`):** **Estúdio Visual completo** (cores, tipografia, cantos, densidade e grade personalizáveis), painel `/vendas`, vendas via Pix e suporte a domínio próprio.
- **Resgate por Gift Card:** ative ou renove planos diretamente em `/planos` ou `/configuracoes-loja` pelo código de ativação.

### Pix EMV, Raio de Entrega & Correios (Meia Versão e Premium)
- Geração automática de **Pix Copia e Cola** padrão BACEN EMV (`CRC-16/CCITT-FALSE`).
- Limite configurável de envio por **Todo o Brasil**, **Mesmo Estado (UF)**, **Mesma Cidade** ou **Raio em km** a partir do CEP de origem.
- Estimativa de frete e prazo **PAC / SEDEX** considerando peso real e peso cubado, com gestão de confirmação e baixa de estoque em `/pedidos`.

### Domínios Gratuitos, Servidor Próprio & Conta Google
- **Buscador de domínios gratuitos:** consulta DNS ao vivo (`dns.google` com fallback `cloudflare-dns.com`) para alternativas como `*.duckdns.org` (com atualização automática DuckDNS), `*.dpdns.org`, `*.pages.dev`, `*.workers.dev` e `*.fly.dev`, aplicando desconto automático na mensalidade.
- **Servidor Próprio & Conta Google Pro:** integração dedicada com Render (`render.yaml`), Google Cloud Run ([deploy-google-cloud.ps1](deploy-google-cloud.ps1)), Fly.io ou VPS Docker via chave segura (`X-YouLink-Sync-Key`), além de verificação OAuth Google Drive/Workspace (`GoogleAccountTierVerifier`).

---

## Estrutura da Solução (`appmaui.blazor.slnx`)

| Projeto | Caminho | Responsabilidade |
| :--- | :--- | :--- |
| **YouLink App** | [appmaui.blazor.csproj](appmaui.blazor.csproj) | Aplicativo `.NET MAUI Blazor Hybrid` para Windows x64 e Android (catálogo, estoque, equipe, pedidos Pix, planos, visual, atualização integrada e publicação 1-clique). |
| **ClothingStore.Web** | [ClothingStore.Web.csproj](src/ClothingStore.Web/ClothingStore.Web.csproj) | Site público da vitrine digital em Blazor Web App com [Dockerfile](src/ClothingStore.Web/Dockerfile) pronto para Render/Cloud Run. |
| **ClothingStore.Api** | [ClothingStore.Api.csproj](src/ClothingStore.Api/ClothingStore.Api.csproj) | ASP.NET Core Web API com autenticação JWT, sincronização segura e endpoints públicos/administrativos ([Dockerfile](src/ClothingStore.Api/Dockerfile)). |
| **ClothingStore.Application** | [ClothingStore.Application.csproj](src/ClothingStore.Application/ClothingStore.Application.csproj) | Casos de uso (`AuthService`, `CatalogService`, `ProductManagementService`, `StockManagementService`, `EmployeeAndSettingsService`, `CloudSyncStateService`, `OrderAndShippingService`). |
| **ClothingStore.Domain** | [ClothingStore.Domain.csproj](src/ClothingStore.Domain/ClothingStore.Domain.csproj) | Entidades e regras de domínio (`User`, `Employee`, `Category`, `Product`, `ProductVariant`, `StockMovement`, `StoreSettings`, `StoreOrder`, `PlanActivationCode`). |
| **ClothingStore.Infrastructure** | [ClothingStore.Infrastructure.csproj](src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj) | Persistência transacional (`ClothingStoreRepository`), segurança PBKDF2/JWT e armazenamento de imagens (local e Supabase Storage). |
| **ClothingStore.Shared** | [ClothingStore.Shared.csproj](src/ClothingStore.Shared/ClothingStore.Shared.csproj) | DTOs, clientes HTTP (`CatalogApiClient`, `AdminApiClient`), `AppUpdateCatalog`, `PixPayloadBuilder`, `BrazilShippingCalculator`, `FreeDomainFinder`, `GoogleAccountTierVerifier` e `WhatsAppLinkBuilder`. |
| **ClothingStore.UnitTests** | [ClothingStore.UnitTests.csproj](tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj) | Suíte com **88 testes automatizados** cobrindo domínio, segurança, stress tests, Pix/Correios, publicação Render em 1 clique, auto-update in-app e validações Windows/NTFS. |

---

## Como Compilar e Testar Localmente

### 1. API (`http://localhost:5180`) e Vitrine Web (`http://localhost:5190`)
```powershell
dotnet run --project src/ClothingStore.Api/ClothingStore.Api.csproj
dotnet run --project src/ClothingStore.Web/ClothingStore.Web.csproj
```

### 2. Gerar Pacotes Windows x64 e Android APK
```powershell
# Windows x64 Self-Contained (unpackaged)
dotnet publish appmaui.blazor.csproj -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -o dist/YouLink-Windows -m:1

# Android APK
dotnet publish appmaui.blazor.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

### 3. Executar a Suíte de Testes (88 testes)
```powershell
dotnet run --project tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj -c Release
```

---

## Banco Supabase & Hospedagem na Nuvem

1. Execute [supabase_schema.sql](database/supabase_schema.sql) no **SQL Editor** do Supabase para ambiente PostgreSQL + Supabase Storage.
2. Utilize [.env.example](.env.example) como referência das variáveis de ambiente.
3. Para deploy rápido gratuito no Render, use [render.yaml](render.yaml) (via botão **Criar Meu Site Grátis 24h** no app); para Google Cloud Run, consulte [deploy-google-cloud.ps1](deploy-google-cloud.ps1).
