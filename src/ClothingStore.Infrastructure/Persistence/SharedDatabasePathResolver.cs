namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Resolve o caminho unificado do banco JSON local no Windows entre YouLink e YouLink Admin
/// (priorizando o workspace local em desenvolvimento e %LOCALAPPDATA%\YouLinkShared em instalações finais).
/// </summary>
public static class SharedDatabasePathResolver
{
    public static string? ResolveExistingDevWorkspaceRoot()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var candidate in new[] { @"C:\Dev\youlink", @"C:\Dev\YouLink", @"C:\Dev\appmaui.blazor" })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string ResolveSharedDatabaseFilePath(string? activeWorkspaceRoot, string appLocalDataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(activeWorkspaceRoot))
            return Path.Combine(activeWorkspaceRoot, "App_Data", "clothingstore_db.json");

        var appLocalDb = Path.Combine(appLocalDataDirectory, "clothingstore_db.json");
        if (!OperatingSystem.IsWindows())
            return appLocalDb;

        var sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouLinkShared",
            "App_Data");
        var sharedDb = Path.Combine(sharedDir, "clothingstore_db.json");

        try
        {
            Directory.CreateDirectory(sharedDir);
            if (!File.Exists(sharedDb) && File.Exists(appLocalDb))
            {
                File.Copy(appLocalDb, sharedDb, overwrite: false);
            }
            return sharedDb;
        }
        catch
        {
            return appLocalDb;
        }
    }
}
