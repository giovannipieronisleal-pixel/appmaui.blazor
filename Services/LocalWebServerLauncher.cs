using System.Diagnostics;
using System.Net.Sockets;

namespace appmaui.blazor.Services;

/// <summary>
/// Garante que o servidor do site público Blazor (ClothingStore.Web na porta 5190)
/// seja iniciado automaticamente em segundo plano quando o aplicativo .NET MAUI estiver rodando no Windows.
/// </summary>
public static class LocalWebServerLauncher
{
    private static Process? _webServerProcess;
    private static readonly SemaphoreSlim _startLock = new(1, 1);

    public static async Task EnsureWebCatalogRunningAsync()
    {
        if (await IsPortOpenAsync("127.0.0.1", 5190))
            return;

        await _startLock.WaitAsync();
        try
        {
            if (await IsPortOpenAsync("127.0.0.1", 5190))
                return;

            var webProjectDir = @"C:\Dev\appmaui.blazor\src\ClothingStore.Web";
            var webExePath = Path.Combine(webProjectDir, "bin", "Debug", "net10.0", "ClothingStore.Web.exe");

            if (File.Exists(webExePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = webExePath,
                    Arguments = "--urls=http://localhost:5190",
                    WorkingDirectory = webProjectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _webServerProcess = Process.Start(psi);
            }
            else if (Directory.Exists(webProjectDir))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{Path.Combine(webProjectDir, "ClothingStore.Web.csproj")}\" --no-build --urls=http://localhost:5190",
                    WorkingDirectory = webProjectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _webServerProcess = Process.Start(psi);
            }

            // Aguarda até 3 segundos para a porta 5190 ficar pronta antes de abrir o navegador
            for (int i = 0; i < 15; i++)
            {
                if (await IsPortOpenAsync("127.0.0.1", 5190))
                    break;
                await Task.Delay(200);
            }
        }
        catch
        {
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

