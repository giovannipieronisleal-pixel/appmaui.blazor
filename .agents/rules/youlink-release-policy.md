# Regra de Atualização e Release do YouLink

Sempre que atualizar o aplicativo YouLink:

1. **Incrementar a versão de forma sincronizada** antes do build/commit:
   - `ApplicationDisplayVersion` e `ApplicationVersion` em `appmaui.blazor.csproj`
   - `AppUpdateCatalog.CurrentAppVersion` em `src/ClothingStore.Shared/Helpers/AppUpdateCatalog.cs`
2. **Validar builds e testes** (`ClothingStore.UnitTests`, `ClothingStore.Api`, `ClothingStore.Web`, `net10.0-windows10.0.19041.0` e `net10.0-android`).
3. **Sempre dar commit e `git push origin master`** (nunca enviar `.vscode/`, `App_Data/` ou `src/ClothingStore.AdminApp/`).
4. **Publicar o novo Release e apagar os releases antigos do GitHub**:
   - O workflow `.github/workflows/release-youlink.yml` gera o APK Android e o pacote Windows x64 a cada push no `master`, cria/atualiza o Release `v<ApplicationDisplayVersion>` e apaga automaticamente os releases anteriores (`gh release delete ... --yes`).
   - Sempre confirmar ao final que o novo Release foi publicado e que os releases antigos foram removidos do GitHub Releases.
