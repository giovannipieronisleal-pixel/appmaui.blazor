using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Garante que o site público do catálogo (porta 5190 — http://localhost:5190/loja/{slug})
/// funcione no navegador tanto no Windows quanto no Android/iOS (celular),
/// servindo o catálogo completo em tempo real diretamente pelo aplicativo quando necessário.
/// </summary>
public static class LocalWebServerLauncher
{
    public const int DefaultPort = 5190;

    private static CatalogService? _catalogService;
    private static TcpListener? _embeddedListener;
    private static Process? _webServerProcess;
    private static readonly SemaphoreSlim _startLock = new(1, 1);

    public static void Initialize(CatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public static async Task OpenCatalogInBrowserAsync(string url)
    {
        await EnsureWebCatalogRunningAsync();

        var uri = new Uri(url);
        try
        {
            // BrowserLaunchMode.SystemPreferred abre o navegador (Chrome Custom Tabs no Android)
            // mantendo o processo do app ativo em primeiro plano para responder imediatamente na porta 5190.
            await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
        }
        catch
        {
            await Launcher.Default.OpenAsync(uri);
        }
    }

    public static async Task EnsureWebCatalogRunningAsync()
    {
        if (await IsPortOpenAsync("127.0.0.1", DefaultPort))
            return;

        await _startLock.WaitAsync();
        try
        {
            if (await IsPortOpenAsync("127.0.0.1", DefaultPort))
                return;

            // 1. Inicia o servidor HTTP embutido multiplataforma (funciona no Android, iOS e Windows instantaneamente)
            if (_catalogService is not null && TryStartEmbeddedHttpServer())
            {
                for (int i = 0; i < 10; i++)
                {
                    if (await IsPortOpenAsync("127.0.0.1", DefaultPort))
                        return;
                    await Task.Delay(50);
                }
            }

            // 2. Fallback para o executável ClothingStore.Web no Windows caso o serviço não tenha sido injetado
            if (OperatingSystem.IsWindows())
            {
                var webProjectDir = @"C:\Dev\appmaui.blazor\src\ClothingStore.Web";
                var webExePath = Path.Combine(webProjectDir, "bin", "Debug", "net10.0", "ClothingStore.Web.exe");

                if (File.Exists(webExePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = webExePath,
                        Arguments = $"--urls=http://localhost:{DefaultPort}",
                        WorkingDirectory = webProjectDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    _webServerProcess = Process.Start(psi);
                }
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

    private static bool TryStartEmbeddedHttpServer()
    {
        try
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Any, DefaultPort);
                listener.Start();
            }
            catch
            {
                listener = new TcpListener(IPAddress.Loopback, DefaultPort);
                listener.Start();
            }

            _embeddedListener = listener;
            _ = Task.Run(() => AcceptConnectionsLoopAsync(listener));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task AcceptConnectionsLoopAsync(TcpListener listener)
    {
        while (true)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleHttpClientAsync(client));
            }
            catch
            {
                break;
            }
        }
    }

    private static async Task HandleHttpClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                // Consome cabeçalhos HTTP
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
                {
                }

                var parts = requestLine.Split(' ');
                var rawTarget = parts.Length >= 2 ? parts[1] : "/";

                var html = await RenderCatalogResponseAsync(rawTarget);
                var bodyBytes = Encoding.UTF8.GetBytes(html);

                var responseHeader =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                    "X-Content-Type-Options: nosniff\r\n" +
                    "Connection: close\r\n\r\n";

                var headerBytes = Encoding.ASCII.GetBytes(responseHeader);
                await stream.WriteAsync(headerBytes);
                await stream.WriteAsync(bodyBytes);
                await stream.FlushAsync();
            }
            catch
            {
            }
        }
    }

    private static async Task<string> RenderCatalogResponseAsync(string rawTarget)
    {
        if (_catalogService is null)
            return "<html><body>Catálogo indisponível.</body></html>";

        var uri = new Uri($"http://localhost:{DefaultPort}{(rawTarget.StartsWith('/') ? rawTarget : "/" + rawTarget)}");
        var path = uri.AbsolutePath.Trim('/');
        var query = ParseQueryString(uri.Query);

        var store = await _catalogService.GetPublicStoreSettingsAsync();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Verifica se é rota de detalhe de produto: /produto/{slug} ou /loja/{storeSlug}/produto/{slug}
        string? productSlug = null;
        if (segments.Length >= 2 && string.Equals(segments[0], "produto", StringComparison.OrdinalIgnoreCase))
        {
            productSlug = Uri.UnescapeDataString(segments[1]);
        }
        else if (segments.Length >= 4 &&
                 string.Equals(segments[0], "loja", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(segments[2], "produto", StringComparison.OrdinalIgnoreCase))
        {
            productSlug = Uri.UnescapeDataString(segments[3]);
        }

        if (!string.IsNullOrWhiteSpace(productSlug))
        {
            var detail = await _catalogService.GetPublicProductDetailAsync(productSlug);
            return RenderProductDetailHtml(store, detail);
        }

        var search = query.GetValueOrDefault("q");
        var categorySlug = query.GetValueOrDefault("cat");
        var color = query.GetValueOrDefault("color");
        var size = query.GetValueOrDefault("size");
        var onlyInStock = string.Equals(query.GetValueOrDefault("inStock"), "true", StringComparison.OrdinalIgnoreCase);

        var filters = await _catalogService.GetCatalogFiltersAsync();
        var products = await _catalogService.GetCatalogProductsAsync(new CatalogQueryFilter(
            Search: search,
            CategorySlug: categorySlug,
            Color: color,
            Size: size,
            OnlyInStock: onlyInStock));

        return RenderCatalogListHtml(store, filters, products, search, categorySlug, color, size, onlyInStock);
    }

    private static string RenderCatalogListHtml(
        PublicStoreSettingsDto store,
        CatalogFiltersDto filters,
        IReadOnlyList<CatalogProductCardDto> products,
        string? search,
        string? categorySlug,
        string? color,
        string? size,
        bool onlyInStock)
    {
        var ptBr = new CultureInfo("pt-BR");
        var storeHomeUrl = $"/loja/{H(store.StoreSlug)}";
        var generalWaUrl = WhatsAppLinkBuilder.BuildGeneralStoreLink(store.WhatsappNumber, store.StoreName);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
        sb.AppendLine($"<title>{H(store.StoreName)} — Catálogo Digital</title>");
        sb.AppendLine(GetEmbeddedStyles());
        sb.AppendLine("</head><body>");

        // Header
        sb.AppendLine("<header class=\"site-header\"><div class=\"container header-inner\">");
        sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"brand\">");
        if (!string.IsNullOrWhiteSpace(store.LogoPublicUrl))
        {
            sb.AppendLine($"<img src=\"{H(store.LogoPublicUrl)}\" alt=\"{H(store.StoreName)}\" class=\"brand-logo\"/>");
        }
        sb.AppendLine($"<span>{H(store.StoreName)}</span></a>");
        sb.AppendLine($"<a href=\"{H(generalWaUrl)}\" target=\"_blank\" rel=\"noopener\" class=\"btn-wa-top\">Atendimento WhatsApp</a>");
        sb.AppendLine("</div></header>");

        // Hero & Filters
        sb.AppendLine("<section class=\"hero\"><div class=\"container\">");
        sb.AppendLine($"<div class=\"eyebrow\">Catálogo Digital Oficial &bull; /loja/{H(store.StoreSlug)}</div>");
        sb.AppendLine($"<h1>{H(store.StoreName)}</h1>");
        sb.AppendLine($"<p class=\"hero-desc\">{H(store.StoreDescription)}</p>");

        sb.AppendLine($"<form method=\"get\" action=\"{storeHomeUrl}\" class=\"filter-bar\">");
        sb.AppendLine("<div><label>Buscar peça</label>");
        sb.AppendLine($"<input type=\"text\" name=\"q\" value=\"{H(search)}\" placeholder=\"Ex.: Camiseta, Vestido...\"/></div>");

        sb.AppendLine("<div><label>Categoria</label><select name=\"cat\" onchange=\"this.form.submit()\">");
        sb.AppendLine("<option value=\"\">Todas as categorias</option>");
        foreach (var c in filters.Categories)
        {
            var sel = string.Equals(c.Slug, categorySlug, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.AppendLine($"<option value=\"{H(c.Slug)}\"{sel}>{H(c.Name)} ({c.ProductCount})</option>");
        }
        sb.AppendLine("</select></div>");

        sb.AppendLine("<div><label>Tamanho</label><select name=\"size\" onchange=\"this.form.submit()\">");
        sb.AppendLine("<option value=\"\">Todos</option>");
        foreach (var s in filters.Sizes)
        {
            var sel = string.Equals(s, size, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.AppendLine($"<option value=\"{H(s)}\"{sel}>{H(s)}</option>");
        }
        sb.AppendLine("</select></div>");

        sb.AppendLine("<div><label>Cor</label><select name=\"color\" onchange=\"this.form.submit()\">");
        sb.AppendLine("<option value=\"\">Todas</option>");
        foreach (var c in filters.Colors)
        {
            var sel = string.Equals(c.Color, color, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.AppendLine($"<option value=\"{H(c.Color)}\"{sel}>{H(c.Color)}</option>");
        }
        sb.AppendLine("</select></div>");

        var chk = onlyInStock ? " checked" : "";
        sb.AppendLine("<div class=\"filter-actions\">");
        sb.AppendLine($"<label class=\"chk\"><input type=\"checkbox\" name=\"inStock\" value=\"true\"{chk} onchange=\"this.form.submit()\"/> Em estoque</label>");
        sb.AppendLine($"<button type=\"submit\" class=\"btn-dark\">Filtrar</button>");
        sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"btn-outline\">Limpar</a>");
        sb.AppendLine("</div></form></div></section>");

        // Products Grid
        sb.AppendLine("<main class=\"container main-section\">");
        sb.AppendLine($"<h2 class=\"section-title\">Peças do Catálogo ({products.Count})</h2>");
        if (products.Count == 0)
        {
            sb.AppendLine("<div class=\"empty-box\">Nenhuma peça encontrada com os filtros selecionados.</div>");
        }
        else
        {
            sb.AppendLine("<div class=\"grid\">");
            foreach (var p in products)
            {
                var detailUrl = $"/loja/{H(store.StoreSlug)}/produto/{H(p.Slug)}";
                sb.AppendLine("<article class=\"card\">");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"card-img\">");
                if (!string.IsNullOrWhiteSpace(p.PrimaryImageUrl))
                {
                    sb.AppendLine($"<img src=\"{H(p.PrimaryImageUrl)}\" alt=\"{H(p.Name)}\"/>");
                }
                else
                {
                    sb.AppendLine("<div class=\"no-img\">Sem foto</div>");
                }
                sb.AppendLine(p.HasAvailableStock
                    ? "<span class=\"badge badge-ok\">Disponível</span>"
                    : "<span class=\"badge badge-out\">Esgotado</span>");
                sb.AppendLine("</a>");

                sb.AppendLine("<div class=\"card-body\">");
                sb.AppendLine($"<div class=\"cat\">{H(p.CategoryName)}</div>");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"title\">{H(p.Name)}</a>");
                sb.AppendLine($"<div class=\"price\">{p.BasePrice.ToString("C", ptBr)}</div>");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"btn-dark full\">Ver Detalhes &amp; Consultar</a>");
                sb.AppendLine("</div></article>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    private static string RenderProductDetailHtml(PublicStoreSettingsDto store, ProductDetailDto? product)
    {
        var storeHomeUrl = $"/loja/{H(store.StoreSlug)}";
        if (product is null)
        {
            return $"<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>{GetEmbeddedStyles()}</head><body><div class=\"container\" style=\"padding:3rem 1rem;\"><h2>Produto não encontrado</h2><a href=\"{storeHomeUrl}\" class=\"btn-dark\">Voltar ao catálogo</a></div></body></html>";
        }

        var ptBr = new CultureInfo("pt-BR");
        var firstImg = product.Images.FirstOrDefault()?.PublicUrl ?? "";
        var firstVariant = product.Variants.FirstOrDefault(v => v.StockQuantity > 0) ?? product.Variants.FirstOrDefault();
        var defaultColor = firstVariant?.Color ?? "";
        var defaultSize = firstVariant?.Size ?? "";
        var initialWaUrl = WhatsAppLinkBuilder.BuildProductConsultationLink(store.WhatsappNumber, product.Name, defaultColor, defaultSize);
        var initialWaMsg = WhatsAppLinkBuilder.BuildProductConsultationMessage(product.Name, defaultColor, defaultSize);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
        sb.AppendLine($"<title>{H(product.Name)} — {H(store.StoreName)}</title>");
        sb.AppendLine(GetEmbeddedStyles());
        sb.AppendLine("</head><body>");

        sb.AppendLine("<header class=\"site-header\"><div class=\"container header-inner\">");
        sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"brand\"><span>{H(store.StoreName)}</span></a>");
        sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"btn-outline\">&larr; Voltar ao Catálogo</a>");
        sb.AppendLine("</div></header>");

        sb.AppendLine("<main class=\"container detail-wrap\">");
        // Coluna Foto
        sb.AppendLine("<div>");
        sb.AppendLine("<div class=\"detail-main-img\">");
        if (!string.IsNullOrWhiteSpace(firstImg))
        {
            sb.AppendLine($"<img id=\"mainPhoto\" src=\"{H(firstImg)}\" alt=\"{H(product.Name)}\"/>");
        }
        else
        {
            sb.AppendLine("<div class=\"no-img\">Sem foto</div>");
        }
        sb.AppendLine("</div>");
        if (product.Images.Count > 1)
        {
            sb.AppendLine("<div class=\"thumbs\">");
            foreach (var img in product.Images)
            {
                sb.AppendLine($"<img src=\"{H(img.PublicUrl)}\" alt=\"{H(product.Name)}\" onclick=\"document.getElementById('mainPhoto').src=this.src\"/>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");

        // Coluna Informações + WhatsApp
        sb.AppendLine("<div class=\"detail-panel\">");
        sb.AppendLine($"<div class=\"cat\">{H(product.CategoryName)}</div>");
        sb.AppendLine($"<h1 style=\"margin:0.3rem 0;\">{H(product.Name)}</h1>");
        sb.AppendLine($"<div class=\"price\" style=\"font-size:1.55rem;margin-bottom:1rem;\">{product.BasePrice.ToString("C", ptBr)}</div>");
        sb.AppendLine($"<p style=\"color:#57534E;line-height:1.6;\">{H(product.Description)}</p>");

        if (product.Variants.Count > 0)
        {
            var colors = product.Variants.GroupBy(v => v.Color, StringComparer.OrdinalIgnoreCase).Select(g => g.Key).ToList();
            var sizes = product.Variants.Select(v => v.Size).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            sb.AppendLine("<div style=\"margin:1rem 0;\"><label>1. Selecione a Cor: <strong id=\"lblColor\">" + H(defaultColor) + "</strong></label><div class=\"chips\">");
            foreach (var c in colors)
            {
                sb.AppendLine($"<button type=\"button\" class=\"chip\" onclick=\"setColor('{HJs(c)}')\">{H(c)}</button>");
            }
            sb.AppendLine("</div></div>");

            sb.AppendLine("<div style=\"margin:1rem 0;\"><label>2. Selecione o Tamanho: <strong id=\"lblSize\">" + H(defaultSize) + "</strong></label><div class=\"chips\">");
            foreach (var s in sizes)
            {
                sb.AppendLine($"<button type=\"button\" class=\"chip\" onclick=\"setSize('{HJs(s)}')\">{H(s)}</button>");
            }
            sb.AppendLine("</div></div>");
        }

        sb.AppendLine($"<a id=\"waBtn\" href=\"{H(initialWaUrl)}\" target=\"_blank\" rel=\"noopener\" class=\"btn-wa-big\">Consultar pelo WhatsApp</a>");
        sb.AppendLine("<div class=\"wa-preview\"><strong>Mensagem automática:</strong><div id=\"waMsg\">\"" + H(initialWaMsg) + "\"</div></div>");
        sb.AppendLine("</div></main>");

        sb.AppendLine("<script>");
        sb.AppendLine($"var storePhone = '{HJs(store.WhatsappNumber)}';");
        sb.AppendLine($"var prodName = '{HJs(product.Name)}';");
        sb.AppendLine($"var selColor = '{HJs(defaultColor)}';");
        sb.AppendLine($"var selSize = '{HJs(defaultSize)}';");
        sb.AppendLine("function updateWa(){");
        sb.AppendLine("  var c = selColor ? selColor.toLowerCase() : 'padrão';");
        sb.AppendLine("  var s = selSize ? selSize.toUpperCase() : 'único';");
        sb.AppendLine("  var msg = 'Olá! Tenho interesse no produto ' + prodName + ', na cor ' + c + ' e tamanho ' + s + '. Gostaria de saber mais informações.';");
        sb.AppendLine("  var elMsg = document.getElementById('waMsg'); if(elMsg) elMsg.textContent = '\"' + msg + '\"';");
        sb.AppendLine("  var elBtn = document.getElementById('waBtn'); if(elBtn) elBtn.href = 'https://wa.me/' + storePhone + '?text=' + encodeURIComponent(msg);");
        sb.AppendLine("}");
        sb.AppendLine("function setColor(c){ selColor=c; var el=document.getElementById('lblColor'); if(el) el.textContent=c; updateWa(); }");
        sb.AppendLine("function setSize(s){ selSize=s; var el=document.getElementById('lblSize'); if(el) el.textContent=s; updateWa(); }");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    private static string GetEmbeddedStyles() => """
        <style>
            * { box-sizing: border-box; font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
            body { margin: 0; background: #FAF9F6; color: #18181B; }
            .container { max-width: 1180px; margin: 0 auto; padding: 0 1rem; }
            .site-header { background: #FFFFFF; border-bottom: 1px solid #E7E5E4; position: sticky; top: 0; z-index: 20; }
            .header-inner { display: flex; justify-content: space-between; align-items: center; min-height: 64px; gap: 0.75rem; flex-wrap: wrap; padding: 0.5rem 1rem; }
            .brand { display: flex; align-items: center; gap: 0.65rem; text-decoration: none; color: #18181B; font-weight: 800; font-size: 1.2rem; text-transform: uppercase; }
            .brand-logo { height: 40px; max-width: 140px; object-fit: contain; }
            .btn-wa-top { background: #16A34A; color: #FFF; text-decoration: none; padding: 0.5rem 0.9rem; border-radius: 8px; font-weight: 600; font-size: 0.85rem; }
            .hero { background: linear-gradient(180deg, #FFFFFF 0%, #FAF9F6 100%); border-bottom: 1px solid #E7E5E4; padding: 2rem 0; }
            .eyebrow { font-size: 0.75rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: #78716C; }
            .hero h1 { font-size: 2rem; margin: 0.35rem 0 0.6rem; }
            .hero-desc { color: #57534E; max-width: 680px; margin: 0 0 1.25rem; line-height: 1.5; }
            .filter-bar { background: #FFF; border: 1px solid #E7E5E4; border-radius: 10px; padding: 1rem; display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 0.75rem; align-items: end; }
            .filter-bar label { display: block; font-size: 0.78rem; font-weight: 600; margin-bottom: 0.3rem; color: #57534E; }
            .filter-bar input[type=text], .filter-bar select { width: 100%; padding: 0.55rem 0.7rem; border: 1px solid #D6D3D1; border-radius: 7px; font-size: 0.9rem; background: #FFF; }
            .filter-actions { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
            .chk { display: flex !important; align-items: center; gap: 0.35rem; cursor: pointer; margin: 0 !important; }
            .btn-dark { background: #18181B; color: #FFF; border: none; padding: 0.58rem 0.95rem; border-radius: 7px; font-weight: 600; font-size: 0.86rem; text-decoration: none; display: inline-block; text-align: center; cursor: pointer; }
            .btn-outline { background: #FFF; color: #18181B; border: 1px solid #D6D3D1; padding: 0.55rem 0.85rem; border-radius: 7px; font-weight: 600; font-size: 0.86rem; text-decoration: none; display: inline-block; }
            .full { width: 100%; margin-top: 0.6rem; }
            .main-section { padding: 1.75rem 1rem 3rem; }
            .section-title { font-size: 1.25rem; margin: 0 0 1.2rem; }
            .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 1.25rem; }
            .card { background: #FFF; border: 1px solid #E7E5E4; border-radius: 10px; overflow: hidden; display: flex; flex-direction: column; }
            .card-img { aspect-ratio: 3 / 4; background: #F3F1EC; position: relative; display: block; }
            .card-img img { width: 100%; height: 100%; object-fit: cover; display: block; }
            .no-img { display: flex; align-items: center; justify-content: center; height: 100%; min-height: 220px; color: #78716C; }
            .badge { position: absolute; top: 0.6rem; left: 0.6rem; padding: 0.22rem 0.6rem; border-radius: 999px; font-size: 0.72rem; font-weight: 700; }
            .badge-ok { background: #DCFCE7; color: #166534; }
            .badge-out { background: #FEE2E2; color: #991B1B; }
            .card-body { padding: 0.9rem; display: flex; flex-direction: column; flex: 1; gap: 0.3rem; }
            .cat { font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.06em; color: #78716C; }
            .title { font-weight: 700; font-size: 1rem; color: #18181B; text-decoration: none; }
            .price { font-weight: 800; font-size: 1.08rem; }
            .empty-box { background: #FFF; border: 1px dashed #D6D3D1; border-radius: 10px; padding: 2.5rem; text-align: center; color: #78716C; }
            .detail-wrap { padding: 1.75rem 1rem 3rem; display: grid; grid-template-columns: repeat(auto-fit, minmax(290px, 1fr)); gap: 1.75rem; align-items: start; }
            .detail-main-img { aspect-ratio: 3 / 4; background: #F3F1EC; border-radius: 12px; overflow: hidden; border: 1px solid #E7E5E4; }
            .detail-main-img img { width: 100%; height: 100%; object-fit: cover; display: block; }
            .thumbs { display: flex; gap: 0.5rem; overflow-x: auto; margin-top: 0.65rem; }
            .thumbs img { width: 68px; height: 86px; object-fit: cover; border-radius: 6px; cursor: pointer; border: 1px solid #D6D3D1; }
            .detail-panel { background: #FFF; border: 1px solid #E7E5E4; border-radius: 12px; padding: 1.4rem; }
            .chips { display: flex; gap: 0.45rem; flex-wrap: wrap; margin-top: 0.4rem; }
            .chip { background: #FAF9F6; border: 1px solid #D6D3D1; border-radius: 7px; padding: 0.45rem 0.8rem; font-weight: 600; cursor: pointer; }
            .btn-wa-big { display: block; width: 100%; background: #16A34A; color: #FFF; text-align: center; text-decoration: none; padding: 0.95rem; border-radius: 9px; font-weight: 700; font-size: 1rem; margin-top: 1.25rem; }
            .wa-preview { background: #F0FDF4; border: 1px solid #BBF7D0; border-radius: 8px; padding: 0.85rem; margin-top: 0.9rem; font-size: 0.84rem; color: #166534; }
        </style>
        """;

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return result;

        var trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            result[key] = val;
        }
        return result;
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string HJs(string? text) =>
        (text ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");

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
