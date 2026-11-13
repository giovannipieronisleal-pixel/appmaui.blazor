# YouLink — Catálogo Digital & Painel da Loja (.NET 10)

**YouLink** é uma plataforma para lojas de roupas composta por um **Aplicativo Mobile/Desktop (.NET MAUI Blazor Hybrid)**, um **Site Público (Catálogo Digital em Blazor Web App)** e uma **Web API (ASP.NET Core)** com integração ao WhatsApp, persistência compatível com **PostgreSQL (Supabase)**, imagens no **Supabase Storage** e script pronto para **Google Cloud Run**.

---

## Baixar o App Pronto (GitHub Releases / Actions)

Os instaladores prontos (**Android `.apk`** e **Windows `win-x64 .zip`**) são gerados automaticamente pelo workflow [release-youlink.yml](.github/workflows/release-youlink.yml):

1. **[👉 Abrir a Aba Releases (Baixar Android APK e Windows .zip)](https://github.com/giovannipieronisleal-pixel/yourlink/releases)** — ou vá direto para a **[Última Versão Publicada (Latest Release)](https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest)** para baixar `YouLink-Android.apk` e `YouLink-Windows-x64.zip`.
2. Ou acesse a aba **[GitHub Actions](https://github.com/giovannipieronisleal-pixel/yourlink/actions)** → último workflow executado com sucesso → baixe os **Artifacts** da compilação mais recente.

> **Nota para Android:** por ser uma compilação distribuída diretamente fora da Play Store, ative *"Instalar apps desconhecidos"* ao abrir o arquivo `.apk` no celular.

---

## Planos & Identidade Visual da Vitrine

O **YouLink** conta com três níveis de vitrine selecionáveis no app (`/planos` e `/configuracoes-loja`), **todos já com Servidor 24h + Certificado HTTPS + Endereço/Domínio da loja inclusos sem custo adicional**:

- **Plano Básico (`Basico`)**: tema fixo *Minimalista Luxo* + servidor 24h + HTTPS + endereço da vitrine inclusos, pronto para vender via WhatsApp, com QR Code e estoque em tempo real.
- **Meia Versão (`Intermediario`)**: **5 versões de site pré-prontas** não configuráveis para troca instantânea em 1 clique + servidor 24h + HTTPS + domínio/endereço incluso + **painel Vendas & Quanto Vendeu** (`/vendas`: faturamento por período, peças mais vendidas e vendas WhatsApp/balcão) + **opção ativável de vendas diretas pelo app/site apenas via Pix** (sem cartão e sem necessidade de CNPJ).
- **Premium Sob Medida (`Premium`)**: Estúdio Visual completo (paleta, fontes, cantos, densidade, destaque e grade configurável) + servidor 24h + HTTPS + endereço incluso (com opção de domínio próprio sem taxa extra da plataforma) + **painel Vendas & Quanto Vendeu** (`/vendas`) + **vendas diretas via Pix ativáveis** com vitrine personalizada.

### Vendas Diretas via Pix, Limite de Distância e Cálculo dos Correios (Meia Versão e Premium)

Quando ativado explicitamente pelo administrador em **Configurações da Loja** (`/configuracoes-loja`), informando sua chave Pix e CEP de origem:

- **Checkout apenas via Pix (sem CNPJ / sem cartão)**: gera código **Pix Copia e Cola** padrão BACEN EMV (`CRC-16/CCITT-FALSE`), enquanto o comprador informa nome, WhatsApp e **endereço completo de entrega com CEP**.
- **Alcance e Limite de Distância configuráveis pelo vendedor**: escolha entre enviar para **Todo o Brasil**, **Mesmo Estado (UF)**, **Mesma Cidade** ou **Raio Máximo em km** calculado a partir do CEP de origem.
- **Estimativa de Custos nos Correios (PAC e SEDEX)**: cálculo automático de prazo em dias úteis, peso real vs. peso cubado e faixa tarifária dos Correios na loja, no simulador das configurações e na tela de gestão **Pedidos Pix** (`/pedidos`) para o vendedor confirmar o Pix, abater estoque e organizar o envio (Correios PAC/SEDEX, Motoboy, Transportadora ou Retirada).

### Buscador de Domínios Gratuitos & Plano Mais Barato

O aplicativo e os fluxos de plano (`/planos`, `/contratar-plano` e `/configuracoes-loja`) incluem um buscador automático ao vivo via DNS público (`dns.google` com fallback `cloudflare-dns.com`) que pesquisa alternativas **100% gratuitas** (`*.duckdns.org` com sincronização direta via API oficial DuckDNS, `*.dpdns.org`, `*.pages.dev`, `*.workers.dev`, `*.fly.dev` e endereço incluso `lojas.<slug>.com`):

- Quando encontra um endereço livre, o app exibe a oferta destacada: *"Olha! Este domínio gratuito está disponível — deseja usar ele gratuitamente e ter um plano mais barato?"*
- Ao aceitar usar o domínio gratuito, o valor mensal do plano cai automaticamente (**R$ 10,00/mês mais barato** nos planos Básico, Meia Versão e Premium).

### Categorias Próprias & Movimentação Rápida em 1 Toque

- **Zero categorias de exemplo pré-cadastradas**: a loja inicia limpa para cada cliente criar suas próprias categorias em `/produtos` (`+ Nova Categoria` ou inline ao cadastrar um item).
- **Movimentação fácil de estoque (`/estoque` e `/produtos`)**: botões rápidos **−1 / +1 / +5 / +10** por variante (com filtro por peça, cor, tamanho, SKU e saldo baixo/zerado), motivos pré-prontos clicáveis e histórico auditável.

### Meu Servidor Próprio (Cloud Run / Render / Fly.io) & Verificação Conta Google Pro

- **Modo "Meu Servidor Próprio" (`/configuracoes-loja`)**: conecte uma instância exclusiva da API YouLink hospedada na sua própria conta Google Cloud Run ([deploy-google-cloud.ps1](deploy-google-cloud.ps1)), Render, Fly.io ou VPS Docker com teste de latência em 1 clique (`GET /api/cloud/status`) e chave de sincronização segura (`X-YouLink-Sync-Key` / variável `YOULINK_SYNC_SECRET` em `POST /api/cloud/sync-state`). Reduz a mensalidade dos planos pagos em **−R$ 10,00/mês**.
- **Verificação de Nível da Conta Google (`GoogleAccountTierVerifier`)**: conexão via Google OAuth 2.0 (PKCE no navegador ou Access Token direto) consultando `userinfo` (`email`, `hd`) e `drive/v3/about` (`storageQuota.limit`). Como o Google não expõe API de consumidor para ler planos comerciais "Google One / AI Pro" nem libera Cloud Run dentro do Google One, contas com **1 TB+** no Drive ou domínio **Google Workspace (`hd`)** desbloqueiam automaticamente o selo Pro e **−R$ 5,00/mês extras** nos planos (podendo acumular até **−R$ 25,00/mês** com Domínio Grátis + Servidor Próprio + Conta Google Pro).

---

## Estrutura Pública da Solução (`appmaui.blazor.slnx`)

| Projeto | Caminho | Descrição |
| :--- | :--- | :--- |
| **YouLink (`appmaui.blazor`)** | [appmaui.blazor.csproj](appmaui.blazor.csproj) | Aplicativo `.NET MAUI Blazor Hybrid` (Android, Windows, iOS, MacCatalyst) para gerenciar catálogo, estoque, equipe, pedidos Pix (`/pedidos`), planos e visual da loja. |
| **ClothingStore.Web** | [ClothingStore.Web.csproj](src/ClothingStore.Web/ClothingStore.Web.csproj) | Site público em Blazor Web App funcionando como catálogo digital com integração ao WhatsApp (`https://wa.me/NUMERO?text=MENSAGEM`) e checkout direto via Pix + CEP quando ativado na loja. |
| **ClothingStore.Api** | [ClothingStore.Api.csproj](src/ClothingStore.Api/ClothingStore.Api.csproj) | ASP.NET Core Web API com endpoints públicos e administrativos protegidos por JWT e Policies no backend. Inclui [Dockerfile](src/ClothingStore.Api/Dockerfile). |
| **ClothingStore.Application** | [ClothingStore.Application.csproj](src/ClothingStore.Application/ClothingStore.Application.csproj) | Regras de aplicação (`AuthService`, `CatalogService`, `ProductManagementService`, `StockManagementService`, `EmployeeAndSettingsService`, `OrderAndShippingService`). |
| **ClothingStore.Domain** | [ClothingStore.Domain.csproj](src/ClothingStore.Domain/ClothingStore.Domain.csproj) | Entidades de domínio (`Role`, `User`, `Employee`, `Category`, `Product`, `ProductVariant`, `ProductImage`, `StockMovement`, `StoreSettings`, `StoreOrder`, `PlanActivationCode`). |
| **ClothingStore.Infrastructure** | [ClothingStore.Infrastructure.csproj](src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj) | Repositório transacional (`ClothingStoreRepository`), autenticação JWT/PBKDF2 e armazenamento de imagens. |
| **ClothingStore.Shared** | [ClothingStore.Shared.csproj](src/ClothingStore.Shared/ClothingStore.Shared.csproj) | DTOs, clientes HTTP (`CatalogApiClient`, `AdminApiClient`), design tokens, `PixPayloadBuilder`, `BrazilShippingCalculator`, `FreeDomainFinder`, `GoogleAccountTierVerifier` e helper de links/domínios. |
| **ClothingStore.UnitTests** | [ClothingStore.UnitTests.csproj](tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj) | Suíte com 74 testes automatizados de domínio, segurança, planos, relatórios de vendas (`/vendas`) e baixa automática no estoque, domínios gratuitos (DNS Google/Cloudflare) e plano mais barato, verificação Google OAuth/Drive Pro e servidor próprio (`X-YouLink-Sync-Key`), categorias limpas, Pix EMV, CEP/distância, cálculo dos Correios, pedidos, stress tests e validação Windows x64 / NTFS / API. |

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
# Windows (Executável Self-Contained x64 — não exige .NET pré-instalado no PC do cliente)
dotnet publish appmaui.blazor.csproj -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -o dist/YouLink-Windows -m:1

# Android (APK)
dotnet publish appmaui.blazor.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

### 4. Testes Automatizados (incluindo Suíte Windows)
```powershell
dotnet build tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj -c Release -m:1
$env:YOULINK_WIN_PUBLISH_DIR = "dist/YouLink-Windows"
.\tests\ClothingStore.UnitTests\bin\Release\net10.0\ClothingStore.UnitTests.exe
```

---

## Banco Supabase & Publicação na Nuvem (Camadas Gratuitas Inclusas)

1. Execute [supabase_schema.sql](database/supabase_schema.sql) no **SQL Editor** do Supabase (Free Tier).
2. Configure o bucket `clothing-images` e as variáveis de ambiente seguindo [.env.example](.env.example).
3. Publique uma única instância compartilhada para atender todas as lojas via `/loja/{slug}` em hospedagem gratuita (Render `.onrender.com`, Koyeb `.koyeb.app`, Fly.io `.fly.dev`) ou Google Cloud Run ([deploy-google-cloud.ps1](deploy-google-cloud.ps1)), mantendo servidor e endereço já inclusos dentro do plano sem encarecer para o usuário.
