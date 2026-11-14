using System.Text.Json;
using ClothingStore.Domain.Entities;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Persiste configurações globais do dono da plataforma (chave Pix recebedora de planos e preços)
/// em um arquivo separado (`platform_owner_settings.json`) ao lado do banco da loja.
/// Assim, apagar ou recriar o banco da loja não apaga a chave Pix salva pelo dono no YouLink Admin.
/// </summary>
public static class PlatformOwnerSettingsStore
{
    public const string DefaultPlaceholderPixKey = "picpay.me/sua-conta";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public sealed record Snapshot(
        string PicpayPixKey,
        decimal BasicPlanMonthlyPrice,
        decimal IntermediaryPlanMonthlyPrice,
        decimal PremiumPlanMonthlyPrice,
        DateTimeOffset UpdatedAt);

    public static string GetCompanionPath(string storageFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(storageFilePath));
        return Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory,
            "platform_owner_settings.json");
    }

    public static bool IsPlaceholderPixKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ||
        string.Equals(key.Trim(), DefaultPlaceholderPixKey, StringComparison.OrdinalIgnoreCase);

    public static Snapshot? Load(string storageFilePath)
    {
        try
        {
            var path = GetCompanionPath(storageFilePath);
            if (!File.Exists(path))
                return null;

            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Snapshot>(raw, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveFromStoreSettings(string storageFilePath, StoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var existing = Load(storageFilePath);
        var hasCustomPix = !IsPlaceholderPixKey(settings.PicpayPixKey);
        var hasCustomPrices =
            settings.BasicPlanMonthlyPrice != 49.90m ||
            settings.IntermediaryPlanMonthlyPrice != 89.90m ||
            settings.PremiumPlanMonthlyPrice != 129.90m;

        // Nunca sobrescrever uma chave Pix real já salva com o placeholder padrão ao recriar uma loja.
        if (!hasCustomPix && !hasCustomPrices && existing is null)
            return;

        var effectivePix = hasCustomPix
            ? settings.PicpayPixKey.Trim()
            : (existing is not null && !IsPlaceholderPixKey(existing.PicpayPixKey)
                ? existing.PicpayPixKey
                : DefaultPlaceholderPixKey);

        var snapshot = new Snapshot(
            PicpayPixKey: effectivePix,
            BasicPlanMonthlyPrice: settings.BasicPlanMonthlyPrice,
            IntermediaryPlanMonthlyPrice: settings.IntermediaryPlanMonthlyPrice,
            PremiumPlanMonthlyPrice: settings.PremiumPlanMonthlyPrice,
            UpdatedAt: DateTimeOffset.UtcNow);

        try
        {
            var path = GetCompanionPath(storageFilePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions), System.Text.Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Persistência auxiliar em melhor esforço
        }
    }

    public static bool ApplyToStoreSettingsIfMissingOrDefault(string storageFilePath, StoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var snapshot = Load(storageFilePath);
        if (snapshot is null)
            return false;

        var changed = false;

        if (IsPlaceholderPixKey(settings.PicpayPixKey) && !IsPlaceholderPixKey(snapshot.PicpayPixKey))
        {
            settings.PicpayPixKey = snapshot.PicpayPixKey.Trim();
            changed = true;
        }

        if (settings.BasicPlanMonthlyPrice == 49.90m && snapshot.BasicPlanMonthlyPrice != 49.90m)
        {
            settings.BasicPlanMonthlyPrice = snapshot.BasicPlanMonthlyPrice;
            changed = true;
        }

        if (settings.IntermediaryPlanMonthlyPrice == 89.90m && snapshot.IntermediaryPlanMonthlyPrice != 89.90m)
        {
            settings.IntermediaryPlanMonthlyPrice = snapshot.IntermediaryPlanMonthlyPrice;
            changed = true;
        }

        if (settings.PremiumPlanMonthlyPrice == 129.90m && snapshot.PremiumPlanMonthlyPrice != 129.90m)
        {
            settings.PremiumPlanMonthlyPrice = snapshot.PremiumPlanMonthlyPrice;
            changed = true;
        }

        return changed;
    }
}
