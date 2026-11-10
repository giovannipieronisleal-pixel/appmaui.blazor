@echo off
chcp 65001 >nul
echo =============================================================================
echo   CLOTHING STORE - DEPLOY DO SITE CATALOGO NO GOOGLE CLOUD RUN (24H ONLINE)
echo =============================================================================
echo.

set SERVICE_NAME=clothingstore-catalog
set REGION=southamerica-east1

echo 1. Verificando o Google Cloud SDK (gcloud CLI)...
where gcloud >nul 2>nul
if %errorlevel% neq 0 (
    echo [AVISO] Google Cloud SDK nao localizado no PATH local.
    echo Voce pode instalar o gcloud CLI em: https://cloud.google.com/sdk
    echo ou executar este comando direto no Google Cloud Shell (no navegador):
    echo.
    echo   gcloud run deploy %SERVICE_NAME% --source . --port 8080 --allow-unauthenticated
    echo.
    pause
    exit /b 1
)

echo.
echo 2. Executando deploy no Google Cloud Run...
echo    Serviço: %SERVICE_NAME%
echo    Região:  %REGION% (São Paulo)
echo.

gcloud run deploy %SERVICE_NAME% ^
    --source . ^
    --platform managed ^
    --region %REGION% ^
    --allow-unauthenticated ^
    --port 8080 ^
    --min-instances 0 ^
    --max-instances 2 ^
    --memory 512Mi

echo.
echo =============================================================================
echo   Deploy finalizado!
echo   Copie a URL fornecida pelo Google Cloud Run (ex: https://...a.run.app)
echo   e cole nas "Configurações da Loja" no aplicativo para ativar a sincronia 24h!
echo =============================================================================
pause

