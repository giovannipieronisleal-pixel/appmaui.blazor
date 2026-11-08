@echo off
chcp 65001 >nul
echo ============================================================================
echo   SERVIDOR DA LOJA DE ROUPAS (API + SITE CATÁLOGO PÚBLICO)
echo   Mantém o site no ar mesmo com o aplicativo .NET MAUI fechado!
echo ============================================================================
echo.
echo Iniciando Backend API em: http://localhost:5180
start "ClothingStore.Api" /min cmd /c "cd /d C:\Dev\appmaui.blazor\src\ClothingStore.Api && dotnet run --no-build --urls=http://localhost:5180"

echo Iniciando Site Público em: http://localhost:5190/loja/aura-vestuario
start "ClothingStore.Web" /min cmd /c "cd /d C:\Dev\appmaui.blazor\src\ClothingStore.Web && dotnet run --no-build --urls=http://localhost:5190"

timeout /t 3 >nul
start http://localhost:5190/loja/aura-vestuario
echo.
echo O Site e a API estão rodando em segundo plano!
echo Voce pode fechar o aplicativo .NET MAUI que o site continuara funcionando.
pause

