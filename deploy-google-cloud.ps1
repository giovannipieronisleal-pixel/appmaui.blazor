# =============================================================================
# SCRIPT DE PUBLICAÇÃO AUTOMATIZADA NO GOOGLE CLOUD RUN
# Projeto: Catálogo Oficial de Roupas (ClothingStore.Web)
# =============================================================================

param (
    [string]$ServiceName = "meu-catalogo-loja",
    [string]$Region = "southamerica-east1",
    [string]$Account = "seu-email@gmail.com"
)

Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host "🚀 PUBLICAÇÃO NO GOOGLE CLOUD RUN (SITE CATÁLOGO 24H)" -ForegroundColor Green
Write-Host "☁️ Conta Google Cloud: $Account" -ForegroundColor Yellow
Write-Host "==============================================================" -ForegroundColor Cyan

# 1. Verifica se o gcloud CLI está instalado no sistema
$gcloudInstalled = Get-Command gcloud -ErrorAction SilentlyContinue

if (-not $gcloudInstalled) {
    Write-Host ""
    Write-Host "⚠️ Google Cloud CLI (gcloud) não foi encontrado no PATH do Windows." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Você pode publicar facilmente de duas formas:" -ForegroundColor White
    Write-Host "--------------------------------------------------------------" -ForegroundColor Gray
    Write-Host "👉 OPÇÃO A (Pelo Google Cloud Shell - Direto no Navegador, SEM instalar nada):" -ForegroundColor Green
    Write-Host "  1. Acesse: https://shell.cloud.google.com/?show=terminal com $Account" -ForegroundColor Cyan
    Write-Host "  2. O terminal do Google já tem gcloud e Docker prontos." -ForegroundColor Gray
    Write-Host "  3. Basta clonar/enviar seu repositório e executar:" -ForegroundColor Gray
    Write-Host "     gcloud run deploy $ServiceName --source ./src/ClothingStore.Web --region $Region --allow-unauthenticated" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "👉 OPÇÃO B (Instalar o gcloud no seu computador com 1 comando):" -ForegroundColor Green
    Write-Host "  Execute no PowerShell como Administrador:" -ForegroundColor Gray
    Write-Host "  (New-Object Net.WebClient).DownloadFile('https://dl.google.com/dl/cloudsdk/channels/rapid/GoogleCloudSDKInstaller.exe', '`$env:Temp\GoogleCloudSDKInstaller.exe'); Start-Process '`$env:Temp\GoogleCloudSDKInstaller.exe'" -ForegroundColor Yellow
    Write-Host "--------------------------------------------------------------" -ForegroundColor Gray
    exit 0
}

# 2. Login com a conta do Google Cloud se necessário
Write-Host "`n1. Verificando autenticação com o Google Cloud..." -ForegroundColor Cyan
gcloud config set account $Account --quiet

# 3. Faz o deploy direto da pasta do ClothingStore.Web
Write-Host "`n2. Compilando container e publicando no Cloud Run ($Region)..." -ForegroundColor Cyan
Write-Host "O Google Cloud Build irá construir a imagem Docker e publicar..." -ForegroundColor Gray

gcloud run deploy $ServiceName `
    --source ./src/ClothingStore.Web `
    --region $Region `
    --allow-unauthenticated `
    --quiet

if ($LASTEXITCODE -eq 0) {
    $serviceUrl = gcloud run services describe $ServiceName --region $Region --format 'value(status.url)'
    Write-Host ""
    Write-Host "==============================================================" -ForegroundColor Green
    Write-Host "🎉 SUCESSO! SEU SITE ESTÁ NO AR NO GOOGLE CLOUD!" -ForegroundColor Green
    Write-Host "🌐 Link Oficial 24h na Internet:" -ForegroundColor Cyan
    Write-Host "   $serviceUrl" -ForegroundColor Yellow
    Write-Host "==============================================================" -ForegroundColor Green
    Write-Host "Cole este link nas Configurações da Loja no App para sincronização automática." -ForegroundColor White
} else {
    Write-Host "`n❌ Falha na publicação. Verifique suas permissões no Google Cloud Console." -ForegroundColor Red
}

