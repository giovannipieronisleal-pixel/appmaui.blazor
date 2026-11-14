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
    public static int ActivePort { get; private set; } = DefaultPort;

    private static CatalogService? _catalogService;
    private static OrderAndShippingService? _orderService;
    private static TcpListener? _embeddedListener;
    private static Process? _webServerProcess;
    private static readonly SemaphoreSlim _startLock = new(1, 1);

    public static void Initialize(CatalogService catalogService, OrderAndShippingService? orderService = null)
    {
        _catalogService = catalogService;
        if (orderService is not null)
            _orderService = orderService;
    }

    public static async Task OpenCatalogInBrowserAsync(string url, string? localStoreSlug = null)
    {
        await EnsureWebCatalogRunningAsync();

        var localUrl = $"http://localhost:{ActivePort}";
        if (!string.IsNullOrWhiteSpace(localStoreSlug))
        {
            localUrl = $"http://localhost:{ActivePort}/loja/{localStoreSlug}";
        }
        else if (_catalogService is not null)
        {
            try
            {
                var store = await _catalogService.GetPublicStoreSettingsAsync();
                if (!string.IsNullOrWhiteSpace(store.StoreSlug))
                {
                    localUrl = $"http://localhost:{ActivePort}/loja/{store.StoreSlug}";
                }
            }
            catch
            {
            }
        }

        var targetUrl = url;

        // Se for um domínio externo (não localhost), verifica se ele já está ativo na internet
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if ((host is "localhost" or "127.0.0.1") && uri.Port == DefaultPort && ActivePort != DefaultPort)
            {
                targetUrl = new UriBuilder(uri) { Port = ActivePort }.Uri.ToString();
            }
            else if (host is not "localhost" and not "127.0.0.1")
            {
                var isOnline = false;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
                    var checkRes = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    isOnline = checkRes.IsSuccessStatusCode;
                }
                catch
                {
                    isOnline = false;
                }

                // Se o domínio ainda não tiver apontamento DNS na internet (ex.: lojas.minhaloja.com ainda em registro),
                // abre automaticamente o servidor local para que o usuário consiga ver e testar o catálogo imediatamente!
                if (!isOnline)
                {
                    targetUrl = localUrl;
                }
            }
        }

        var finalUri = new Uri(targetUrl);
        try
        {
            // BrowserLaunchMode.SystemPreferred abre o navegador (Chrome Custom Tabs no Android)
            // mantendo o processo do app ativo em primeiro plano para responder imediatamente na porta 5190.
            await Browser.Default.OpenAsync(finalUri, BrowserLaunchMode.SystemPreferred);
        }
        catch
        {
            await Launcher.Default.OpenAsync(finalUri);
        }
    }

    public static async Task EnsureWebCatalogRunningAsync()
    {
        if (await IsPortOpenAsync("127.0.0.1", ActivePort))
            return;

        await _startLock.WaitAsync();
        try
        {
            if (await IsPortOpenAsync("127.0.0.1", ActivePort))
                return;

            // 1. Inicia o servidor HTTP embutido multiplataforma (funciona no Android, iOS e Windows instantaneamente)
            if (_catalogService is not null && TryStartEmbeddedHttpServer())
            {
                for (int i = 0; i < 10; i++)
                {
                    if (await IsPortOpenAsync("127.0.0.1", ActivePort))
                        return;
                    await Task.Delay(50);
                }
            }

            // 2. Fallback para o executável ClothingStore.Web no Windows caso o serviço não tenha sido injetado
            if (OperatingSystem.IsWindows())
            {
                var packagedWebExe = Path.Combine(AppContext.BaseDirectory, "ClothingStore.Web.exe");
                var webProjectDir = Path.Combine(@"C:\Dev\appmaui.blazor", "src", "ClothingStore.Web");
                var devWebExe = Path.Combine(webProjectDir, "bin", "Debug", "net10.0", "ClothingStore.Web.exe");
                var webExePath = File.Exists(packagedWebExe) ? packagedWebExe : devWebExe;

                if (File.Exists(webExePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = webExePath,
                        Arguments = $"--urls=http://localhost:{ActivePort}",
                        WorkingDirectory = Path.GetDirectoryName(webExePath) ?? AppContext.BaseDirectory,
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
            // Usa Loopback primeiro no Windows para não acionar alerta de firewall nem exigir permissão de administrador
            var listener = TryBindListener(IPAddress.Loopback, DefaultPort)
                ?? (OperatingSystem.IsWindows() ? null : TryBindListener(IPAddress.Any, DefaultPort))
                ?? TryBindListener(IPAddress.Loopback, 0);

            if (listener is null)
                return false;

            ActivePort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _embeddedListener = listener;
            _ = Task.Run(() => AcceptConnectionsLoopAsync(listener));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TcpListener? TryBindListener(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            return listener;
        }
        catch
        {
            return null;
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
                var (requestLine, headers, bodyBytes) = await ReadHttpRequestAsync(stream);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                var parts = requestLine.Split(' ');
                var method = parts.Length >= 1 ? parts[0].ToUpperInvariant() : "GET";
                var rawTarget = parts.Length >= 2 ? parts[1] : "/";

                if (rawTarget.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase))
                {
                    var (statusCode, jsonPayload) = await HandleEmbeddedApiRequestAsync(method, rawTarget, bodyBytes);
                    await WriteHttpResponseAsync(stream, statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(jsonPayload));
                    return;
                }

                var html = await RenderCatalogResponseAsync(rawTarget);
                await WriteHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            }
            catch
            {
            }
        }
    }

    private static async Task<(string? RequestLine, Dictionary<string, string> Headers, byte[] Body)> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(2048);
        var single = new byte[1];
        var headerEndIndex = -1;

        while (buffer.Count < 65536)
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1));
            if (read == 0)
                break;

            buffer.Add(single[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                headerEndIndex = n - 4;
                break;
            }
        }

        if (headerEndIndex <= 0)
            return (null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

        var headerText = Encoding.ASCII.GetString(buffer.Take(headerEndIndex).ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines.Length > 0 ? lines[0] : null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out var lenStr) &&
            int.TryParse(lenStr, out var contentLength) &&
            contentLength > 0 &&
            contentLength <= 1_048_576)
        {
            body = new byte[contentLength];
            var totalRead = 0;
            while (totalRead < contentLength)
            {
                var chunk = await stream.ReadAsync(body.AsMemory(totalRead, contentLength - totalRead));
                if (chunk == 0)
                    break;
                totalRead += chunk;
            }
            if (totalRead < contentLength)
                body = body[..totalRead];
        }

        return (requestLine, headers, body);
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, string statusCode, string contentType, byte[] bodyBytes)
    {
        var responseHeader =
            $"HTTP/1.1 {statusCode}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Referrer-Policy: strict-origin-when-cross-origin\r\n" +
            "Permissions-Policy: camera=(), microphone=(), geolocation=()\r\n" +
            "Content-Security-Policy: default-src 'self'; img-src 'self' https: http: data:; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com data:; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(responseHeader);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(string StatusCode, string Json)> HandleEmbeddedApiRequestAsync(string method, string rawTarget, byte[] bodyBytes)
    {
        if (_orderService is null)
            return ("503 Service Unavailable", JsonSerializer.Serialize(new { error = "Serviço de pedidos Pix indisponível." }, WebJsonOptions));

        try
        {
            var path = rawTarget.Split('?')[0].TrimEnd('/');

            if (method == "GET" && path.StartsWith("/api/public/cep/", StringComparison.OrdinalIgnoreCase))
            {
                var cep = Uri.UnescapeDataString(path["/api/public/cep/".Length..]);
                var dto = await _orderService.LookupCepAsync(cep);
                return ("200 OK", JsonSerializer.Serialize(dto, WebJsonOptions));
            }

            if (method == "POST" && string.Equals(path, "/api/public/shipping-quote", StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<ShippingQuoteRequest>(bodyBytes, WebJsonOptions) ?? new ShippingQuoteRequest();
                var quote = await _orderService.QuoteShippingForBuyerAsync(req);
                return ("200 OK", JsonSerializer.Serialize(quote, WebJsonOptions));
            }

            if (method == "POST" && string.Equals(path, "/api/public/orders/pix", StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<CreatePixOrderRequest>(bodyBytes, WebJsonOptions) ?? new CreatePixOrderRequest();
                req.ExpressMobileMode = false;
                var order = await _orderService.PlacePixOrderAsync(req);
                return ("201 Created", JsonSerializer.Serialize(order, WebJsonOptions));
            }

            return ("404 Not Found", JsonSerializer.Serialize(new { error = "Endpoint não encontrado." }, WebJsonOptions));
        }
        catch (ClothingStore.Domain.Exceptions.DomainException dex)
        {
            return ("400 Bad Request", JsonSerializer.Serialize(new { error = dex.Message }, WebJsonOptions));
        }
        catch (Exception ex)
        {
            return ("400 Bad Request", JsonSerializer.Serialize(new { error = ex.Message }, WebJsonOptions));
        }
    }

    private static async Task<string> RenderCatalogResponseAsync(string rawTarget)
    {
        if (_catalogService is null)
            return "<html><body>Catálogo indisponível.</body></html>";

        var uri = new Uri($"http://localhost:{ActivePort}{(rawTarget.StartsWith('/') ? rawTarget : "/" + rawTarget)}");
        var path = uri.AbsolutePath.Trim('/');
        var query = ParseQueryString(uri.Query);

        var store = await _catalogService.GetPublicStoreSettingsAsync();
        if (!store.IsSiteActive)
        {
            return RenderDeactivatedStoreHtml(store);
        }

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

        var design = StoreDesignCssHelper.Sanitize(store.Design);
        var heroClass = design.HeroLayout switch
        {
            "centered" => "hero hero-centered",
            "banner" => "hero hero-banner",
            _ => "hero"
        };
        var eyebrow = string.IsNullOrWhiteSpace(design.HeroEyebrow)
            ? "Nova Coleção Disponível"
            : H(design.HeroEyebrow);
        var headline = string.IsNullOrWhiteSpace(design.HeroHeadline)
            ? H(store.StoreName)
            : H(design.HeroHeadline);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
        sb.AppendLine($"<title>{H(store.StoreName)} — Catálogo Digital</title>");
        sb.AppendLine(GetEmbeddedStyles(store.Design));
        sb.AppendLine("</head><body>");

        // Header
        sb.AppendLine("<header class=\"site-header\"><div class=\"container header-inner\">");
        sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"brand\">");
        var safeLogoUrl = WhatsAppLinkBuilder.SanitizeMediaUrl(store.LogoPublicUrl);
        if (!string.IsNullOrWhiteSpace(safeLogoUrl))
        {
            sb.AppendLine($"<img src=\"{H(safeLogoUrl)}\" alt=\"{H(store.StoreName)}\" class=\"brand-logo\"/>");
        }
        sb.AppendLine($"<span>{H(store.StoreName)}</span></a>");
        sb.AppendLine($"<a href=\"{H(generalWaUrl)}\" target=\"_blank\" rel=\"noopener\" class=\"btn-wa-top\">Atendimento WhatsApp</a>");
        sb.AppendLine("</div></header>");

        // Hero & Filters
        sb.AppendLine($"<section class=\"{heroClass}\"><div class=\"container\">");
        if (design.ShowLookbookBadge)
        {
            sb.AppendLine($"<div class=\"eyebrow\">{eyebrow}</div>");
        }
        sb.AppendLine($"<h1>{headline}</h1>");
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

        // Category quick pills
        if (filters.Categories.Count > 0)
        {
            sb.AppendLine("<div style=\"display:flex;flex-wrap:wrap;gap:0.5rem;margin-bottom:1.15rem;\">");
            var allClass = string.IsNullOrWhiteSpace(categorySlug) ? "btn-dark" : "btn-outline";
            sb.AppendLine($"<a href=\"{storeHomeUrl}\" class=\"{allClass}\">Todas as peças</a>");
            foreach (var c in filters.Categories)
            {
                var cClass = string.Equals(c.Slug, categorySlug, StringComparison.OrdinalIgnoreCase) ? "btn-dark" : "btn-outline";
                sb.AppendLine($"<a href=\"{storeHomeUrl}?cat={Uri.EscapeDataString(c.Slug)}\" class=\"{cClass}\">{H(c.Name)} ({c.ProductCount})</a>");
            }
            sb.AppendLine("</div>");
        }

        // Products Grid
        sb.AppendLine("<main class=\"container main-section\">");
        sb.AppendLine($"<h2 class=\"section-title\">Roupas Disponíveis ({products.Count})</h2>");
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
                var safePrimaryImg = WhatsAppLinkBuilder.SanitizeMediaUrl(p.PrimaryImageUrl);
                sb.AppendLine("<article class=\"card\">");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"card-img\">");
                if (!string.IsNullOrWhiteSpace(safePrimaryImg))
                {
                    sb.AppendLine($"<img src=\"{H(safePrimaryImg)}\" alt=\"{H(p.Name)}\"/>");
                }
                else
                {
                    sb.AppendLine("<div class=\"no-img\">Sem foto</div>");
                }
                sb.AppendLine(!p.HasAvailableStock
                    ? "<span class=\"badge badge-out\">Esgotado</span>"
                    : p.TotalStockQuantity <= 3
                        ? "<span class=\"badge badge-ok\">Últimas peças</span>"
                        : "<span class=\"badge badge-green\">Disponível</span>");
                sb.AppendLine("</a>");

                sb.AppendLine("<div class=\"card-body\">");
                sb.AppendLine($"<div class=\"cat\">{H(p.CategoryName)}</div>");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"title\">{H(p.Name)}</a>");
                sb.AppendLine($"<div class=\"price\">{p.BasePrice.ToString("C", ptBr)}</div>");
                sb.AppendLine($"<a href=\"{detailUrl}\" class=\"btn-dark full\">Ver Fotos &amp; Escolher Tamanho</a>");
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
            return $"<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>{GetEmbeddedStyles(store.Design)}</head><body><div class=\"container\" style=\"padding:3rem 1rem;\"><h2>Produto não encontrado</h2><a href=\"{storeHomeUrl}\" class=\"btn-dark\">Voltar ao catálogo</a></div></body></html>";
        }

        var ptBr = new CultureInfo("pt-BR");
        var safeGalleryImages = product.Images
            .Select(img => WhatsAppLinkBuilder.SanitizeMediaUrl(img.PublicUrl))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList();
        var firstImg = safeGalleryImages.FirstOrDefault() ?? "";
        var firstVariant = product.Variants.FirstOrDefault(v => v.StockQuantity > 0) ?? product.Variants.FirstOrDefault();
        var defaultColor = firstVariant?.Color ?? "";
        var defaultSize = firstVariant?.Size ?? "";
        var initialWaUrl = WhatsAppLinkBuilder.BuildProductConsultationLink(store.WhatsappNumber, product.Name, defaultColor, defaultSize);
        var initialWaMsg = WhatsAppLinkBuilder.BuildProductConsultationMessage(product.Name, defaultColor, defaultSize);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
        sb.AppendLine($"<title>{H(product.Name)} — {H(store.StoreName)}</title>");
        sb.AppendLine(GetEmbeddedStyles(store.Design));
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
        if (safeGalleryImages.Count > 1)
        {
            sb.AppendLine("<div class=\"thumbs\">");
            foreach (var imgUrl in safeGalleryImages)
            {
                sb.AppendLine($"<img src=\"{H(imgUrl)}\" alt=\"{H(product.Name)}\" onclick=\"document.getElementById('mainPhoto').src=this.src\"/>");
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

        if (store.DirectPixSalesReady && product.Variants.Any(v => v.IsActive && v.StockQuantity > 0))
        {
            var scopeDesc = H(BrazilShippingCalculator.DeliveryScopeModes.Describe(
                store.DeliveryScopeMode,
                store.MaxDeliveryDistanceKm,
                store.OriginCity,
                store.OriginState));

            sb.AppendLine("<div id=\"pixCheckoutBox\" style=\"margin-top:1.25rem;border:1.5px solid #16A34A;border-radius:10px;padding:1rem;background:var(--brand-bg,#FAF8F5);\">");
            sb.AppendLine("<div style=\"display:flex;justify-content:space-between;align-items:center;gap:0.5rem;flex-wrap:wrap;margin-bottom:0.4rem;\"><strong>💸 Comprar Direto pelo App (Apenas Pix)</strong><span style=\"font-size:0.72rem;font-weight:700;background:#DCFCE7;color:#166534;padding:0.18rem 0.55rem;border-radius:999px;\">Exclusivo Pix</span></div>");
            sb.AppendLine($"<p style=\"font-size:0.82rem;color:#57534E;margin:0 0 0.7rem;\">Área de entrega: <strong>{scopeDesc}</strong></p>");
            sb.AppendLine("<div id=\"pixErr\" style=\"display:none;background:rgba(239,68,68,0.12);border:1px solid #EF4444;color:#991B1B;padding:0.55rem 0.7rem;border-radius:6px;font-size:0.82rem;margin-bottom:0.6rem;\"></div>");
            sb.AppendLine("<div style=\"display:flex;gap:0.4rem;flex-wrap:wrap;margin-bottom:0.55rem;\">");
            sb.AppendLine("<input id=\"pxCep\" type=\"text\" placeholder=\"CEP de Entrega (ex.: 01310-100)\" style=\"flex:1;min-width:160px;padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxQty\" type=\"number\" min=\"1\" max=\"50\" value=\"1\" style=\"width:80px;padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\" title=\"Quantidade\"/>");
            sb.AppendLine("<button type=\"button\" class=\"btn-outline\" onclick=\"checkShipping()\">Calcular</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div id=\"pxShipInfo\" style=\"display:none;padding:0.6rem;border-radius:8px;font-size:0.82rem;margin-bottom:0.65rem;\"></div>");
            sb.AppendLine("<div style=\"display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:0.45rem;margin-bottom:0.65rem;\">");
            sb.AppendLine("<input id=\"pxName\" type=\"text\" placeholder=\"Seu Nome Completo *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxWa\" type=\"text\" placeholder=\"Seu WhatsApp (DDD+Número) *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxStreet\" type=\"text\" placeholder=\"Rua / Logradouro *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxNum\" type=\"text\" placeholder=\"Número *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxComp\" type=\"text\" placeholder=\"Complemento\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxNeigh\" type=\"text\" placeholder=\"Bairro *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxCity\" type=\"text\" placeholder=\"Cidade *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;\"/>");
            sb.AppendLine("<input id=\"pxUf\" type=\"text\" maxlength=\"2\" placeholder=\"UF *\" style=\"padding:0.5rem;border:1px solid #D6D3D1;border-radius:6px;text-transform:uppercase;\"/>");
            sb.AppendLine("</div>");
            sb.AppendLine("<button id=\"pxSubmitBtn\" type=\"button\" class=\"btn-dark full\" style=\"background:#16A34A;color:#FFF;padding:0.85rem;font-size:0.95rem;\" onclick=\"submitPixOrder()\">💸 Finalizar Pedido no Pix</button>");
            sb.AppendLine("<div id=\"pxReceipt\" style=\"display:none;background:rgba(22,163,74,0.12);border:1px solid #16A34A;border-radius:8px;padding:0.85rem;margin-top:0.65rem;\"></div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div></main>");

        var variantJson = JsonSerializer.Serialize(product.Variants.Select(v => new { id = v.Id, color = v.Color, size = v.Size, stock = v.StockQuantity }), WebJsonOptions);
        sb.AppendLine("<script>");
        sb.AppendLine($"var prodId = '{product.Id}';");
        sb.AppendLine($"var unitPrice = {product.BasePrice.ToString("0.00", CultureInfo.InvariantCulture)};");
        sb.AppendLine($"var defWeightG = {store.DefaultPackageWeightGrams};");
        sb.AppendLine($"var variantsData = {variantJson};");
        sb.AppendLine($"var storePhone = '{HJs(store.WhatsappNumber)}';");
        sb.AppendLine($"var prodName = '{HJs(product.Name)}';");
        sb.AppendLine($"var selColor = '{HJs(defaultColor)}';");
        sb.AppendLine($"var selSize = '{HJs(defaultSize)}';");
        sb.AppendLine("function getSelectedVariant(){ return variantsData.find(function(v){ return (!selColor || v.color.toLowerCase()===selColor.toLowerCase()) && (!selSize || v.size.toLowerCase()===selSize.toLowerCase()) && v.stock>0; }) || variantsData[0]; }");
        sb.AppendLine("function updateWa(){");
        sb.AppendLine("  var c = selColor ? selColor.toLowerCase() : 'padrão';");
        sb.AppendLine("  var s = selSize ? selSize.toUpperCase() : 'único';");
        sb.AppendLine("  var msg = 'Olá! Tenho interesse no produto ' + prodName + ', na cor ' + c + ' e tamanho ' + s + '. Gostaria de saber mais informações.';");
        sb.AppendLine("  var elMsg = document.getElementById('waMsg'); if(elMsg) elMsg.textContent = '\"' + msg + '\"';");
        sb.AppendLine("  var elBtn = document.getElementById('waBtn'); if(elBtn) elBtn.href = 'https://wa.me/' + storePhone + '?text=' + encodeURIComponent(msg);");
        sb.AppendLine("}");
        sb.AppendLine("function setColor(c){ selColor=c; var el=document.getElementById('lblColor'); if(el) el.textContent=c; updateWa(); }");
        sb.AppendLine("function setSize(s){ selSize=s; var el=document.getElementById('lblSize'); if(el) el.textContent=s; updateWa(); }");
        sb.AppendLine("function escHtml(v){ return String(v==null?'':v).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\"/g,'&quot;').replace(/'/g,'&#39;'); }");
        sb.AppendLine("async function checkShipping(){ var err=document.getElementById('pxErr'); var box=document.getElementById('pxShipInfo'); err.style.display='none'; box.style.display='none'; var cep=document.getElementById('pxCep').value; var qty=parseInt(document.getElementById('pxQty').value||'1'); try{ var r1=await fetch('/api/public/cep/'+encodeURIComponent(cep)); if(r1.ok){ var c=await r1.json(); if(c.street && !document.getElementById('pxStreet').value) document.getElementById('pxStreet').value=c.street; if(c.neighborhood && !document.getElementById('pxNeigh').value) document.getElementById('pxNeigh').value=c.neighborhood; if(c.city) document.getElementById('pxCity').value=c.city; if(c.state) document.getElementById('pxUf').value=c.state; } var r2=await fetch('/api/public/shipping-quote',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({destinationCep:cep,destinationCity:document.getElementById('pxCity').value,destinationState:document.getElementById('pxUf').value,weightGrams:defWeightG*Math.max(1,qty)})}); var data=await r2.json(); if(!r2.ok) throw new Error(data.error||'Erro ao calcular'); box.style.display='block'; box.style.background=data.eligibility.isAllowed?'rgba(22,163,74,0.12)':'rgba(239,68,68,0.12)'; box.style.border=data.eligibility.isAllowed?'1px solid #16A34A':'1px solid #EF4444'; var h='<strong>'+(data.eligibility.isAllowed?'✓ Endereço atendido: ':'✕ Fora do limite: ')+'</strong>'+escHtml(data.eligibility.reasonMessage); if(data.eligibility.isAllowed && data.correiosQuote){ h+='<br/>Correios estimado: PAC R$ '+Number(data.correiosQuote.pacPrice||0).toFixed(2)+' | SEDEX R$ '+Number(data.correiosQuote.sedexPrice||0).toFixed(2); } box.innerHTML=h; }catch(e){ err.style.display='block'; err.textContent=e.message; } }");
        sb.AppendLine("async function submitPixOrder(){ var err=document.getElementById('pxErr'); var rec=document.getElementById('pxReceipt'); err.style.display='none'; var v=getSelectedVariant(); if(!v){ err.style.display='block'; err.textContent='Selecione uma variante com estoque.'; return; } try{ var payload={productId:prodId,productVariantId:v.id,quantity:parseInt(document.getElementById('pxQty').value||'1'),customerName:document.getElementById('pxName').value,customerWhatsapp:document.getElementById('pxWa').value,cep:document.getElementById('pxCep').value,street:document.getElementById('pxStreet').value,number:document.getElementById('pxNum').value,complement:document.getElementById('pxComp').value,neighborhood:document.getElementById('pxNeigh').value,city:document.getElementById('pxCity').value,state:document.getElementById('pxUf').value}; var r=await fetch('/api/public/orders/pix',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)}); var o=await r.json(); if(!r.ok) throw new Error(o.error||'Falha ao gerar pedido Pix'); rec.style.display='block'; rec.innerHTML='<strong>✅ Pedido '+escHtml(o.orderNumber)+' gerado!</strong><br/>Total Pix: R$ '+Number(o.totalPixAmount||0).toFixed(2)+'<br/>Chave Pix: <code>'+escHtml(o.pixKey)+'</code><br/>Código Copia e Cola:<textarea readonly rows=\"3\" style=\"width:100%;margin-top:0.35rem;font-family:monospace;font-size:0.75rem;\">'+escHtml(o.pixBrCode)+'</textarea>'; }catch(e){ err.style.display='block'; err.textContent=e.message; } }");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    private static string RenderDeactivatedStoreHtml(PublicStoreSettingsDto store)
    {
        var generalWaUrl = WhatsAppLinkBuilder.BuildGeneralStoreLink(store.WhatsappNumber, store.StoreName);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
        sb.AppendLine($"<title>{H(store.StoreName)} — Loja Desativada</title>");
        sb.AppendLine(GetEmbeddedStyles(store.Design));
        sb.AppendLine("</head><body>");
        sb.AppendLine("<header class=\"site-header\"><div class=\"container header-inner\">");
        sb.AppendLine($"<div class=\"brand\"><span>{H(store.StoreName)}</span></div>");
        sb.AppendLine("</div></header>");
        sb.AppendLine("<main class=\"container\" style=\"padding: 4rem 1rem; text-align: center; max-width: 680px;\">");
        sb.AppendLine("<div style=\"background: var(--brand-surface, #ffffff); color: var(--brand-text, #18181B); border: 1.5px solid var(--brand-border, #e2e8f0); border-radius: 16px; padding: 3rem 2rem; box-shadow: 0 10px 30px rgba(0,0,0,0.08);\">");
        sb.AppendLine("<div style=\"font-size: 3.5rem; margin-bottom: 1rem;\">🛍️</div>");
        sb.AppendLine("<h1 style=\"font-family: var(--brand-font-display, Georgia, serif); font-size: 1.85rem; font-weight: 700; color: var(--brand-text, #0f172a); margin-bottom: 0.75rem;\">Loja em Atualização</h1>");
        sb.AppendLine($"<p style=\"font-size: 1.05rem; color: var(--brand-text-muted, #475569); line-height: 1.6; margin-bottom: 1.5rem;\">{H(store.DeactivationReason ?? "Estamos preparando novas peças para nossa vitrine!")}</p>");
        if (!string.IsNullOrWhiteSpace(store.WhatsappNumber))
        {
            sb.AppendLine($"<a href=\"{H(generalWaUrl)}\" target=\"_blank\" rel=\"noopener\" class=\"btn-wa-top\" style=\"display: inline-block; padding: 0.85rem 1.75rem; font-size: 1rem;\">💬 Falar com a Loja no WhatsApp</a>");
        }
        sb.AppendLine("</div></main></body></html>");
        return sb.ToString();
    }

    private static string GetEmbeddedStyles(StoreDesignDto? design = null)
    {
        var fontsHref = H(StoreDesignCssHelper.BuildGoogleFontsUrl(design));
        var rootVars = StoreDesignCssHelper.BuildRootCssBlock(design);

        return $$"""
        <link rel="stylesheet" href="{{fontsHref}}" />
        <style>
            {{rootVars}}
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--brand-bg, #FAF8F5); color: var(--brand-text, #18181B); font-family: var(--brand-font-body, system-ui, sans-serif); line-height: 1.55; }
            .container { max-width: 1240px; margin: 0 auto; padding: 0 1.25rem; }
            .site-header { background: var(--brand-header-glass, rgba(255,255,255,0.9)); backdrop-filter: blur(12px); border-bottom: 1px solid var(--brand-border, #E7E2DA); position: sticky; top: 0; z-index: 20; }
            .header-inner { display: flex; justify-content: space-between; align-items: center; min-height: 70px; gap: 0.75rem; flex-wrap: wrap; padding: 0.5rem 1.25rem; }
            .brand { display: flex; align-items: center; gap: 0.75rem; text-decoration: none; color: var(--brand-text, #18181B); font-family: var(--brand-font-display, Georgia, serif); font-weight: 600; font-size: 1.28rem; letter-spacing: -0.015em; }
            .brand-logo { height: 40px; max-width: 140px; object-fit: contain; border-radius: var(--brand-radius-btn, 6px); }
            .btn-wa-top { background: #16A34A; color: #FFF; text-decoration: none; padding: 0.55rem 1.05rem; border-radius: var(--brand-radius-btn, 6px); font-weight: 600; font-size: 0.85rem; }
            .hero { background: var(--brand-surface, #FFFFFF); border-bottom: 1px solid var(--brand-border, #E7E2DA); padding: 2.5rem 0; }
            .hero-centered .container { text-align: center; }
            .hero-centered .hero-desc { margin-left: auto; margin-right: auto; }
            .hero-banner { background: var(--brand-sidebar, #141416); color: #FAF8F5; border-bottom: 1px solid var(--brand-accent, #B69158); }
            .hero-banner h1 { color: #FAF8F5; }
            .hero-banner .hero-desc { color: #D6D0C4; }
            .eyebrow { font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.2em; color: var(--brand-accent, #B69158); }
            .hero h1 { font-family: var(--brand-font-display, Georgia, serif); font-size: clamp(2rem, 4vw, 3.1rem); font-weight: 500; margin: 0.45rem 0 0.7rem; line-height: 1.1; }
            .hero-desc { color: var(--brand-text-muted, #57534E); max-width: 680px; margin: 0 0 1.4rem; line-height: 1.6; }
            .filter-bar { background: var(--brand-surface, #FFF); border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-card, 8px); padding: 1.1rem; display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 0.75rem; align-items: end; color: var(--brand-text, #18181B); }
            .filter-bar label { display: block; font-size: 0.74rem; text-transform: uppercase; letter-spacing: 0.1em; font-weight: 700; margin-bottom: 0.35rem; color: var(--brand-text-muted, #57534E); }
            .filter-bar input[type=text], .filter-bar select { width: 100%; padding: 0.58rem 0.75rem; border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-btn, 6px); font-size: 0.9rem; background: var(--brand-bg, #FAF8F5); color: var(--brand-text, #18181B); }
            .filter-actions { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
            .chk { display: flex !important; align-items: center; gap: 0.35rem; cursor: pointer; margin: 0 !important; }
            .btn-dark { background: var(--brand-primary, #18181B); color: var(--brand-primary-fg, #FAF8F5); border: none; padding: 0.62rem 1.05rem; border-radius: var(--brand-radius-btn, 6px); font-weight: 600; font-size: 0.86rem; text-decoration: none; display: inline-block; text-align: center; cursor: pointer; }
            .btn-outline { background: transparent; color: var(--brand-text, #18181B); border: 1px solid var(--brand-border, #E7E2DA); padding: 0.58rem 0.9rem; border-radius: var(--brand-radius-btn, 6px); font-weight: 600; font-size: 0.86rem; text-decoration: none; display: inline-block; }
            .full { width: 100%; margin-top: 0.6rem; }
            .main-section { padding: 2rem 1.25rem 3.5rem; }
            .section-title { font-family: var(--brand-font-display, Georgia, serif); font-size: 1.5rem; font-weight: 500; margin: 0 0 1.25rem; }
            .grid { display: grid; grid-template-columns: repeat(var(--brand-grid-cols, 3), minmax(0, 1fr)); gap: var(--brand-gap, 1.35rem); }
            @media (max-width: 900px) { .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
            @media (max-width: 560px) { .grid { grid-template-columns: 1fr; } }
            .card { background: var(--brand-surface, #FFF); border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-card, 8px); overflow: hidden; display: flex; flex-direction: column; }
            .card-img { aspect-ratio: 4 / 5; background: var(--brand-surface-subtle, #F3EFE8); position: relative; display: block; overflow: hidden; }
            .card-img img { width: 100%; height: 100%; object-fit: cover; display: block; }
            .no-img { display: flex; align-items: center; justify-content: center; height: 100%; min-height: 220px; color: var(--brand-text-muted, #78716C); font-family: var(--brand-font-display, serif); font-style: italic; }
            .badge { position: absolute; top: 0.65rem; left: 0.65rem; padding: 0.24rem 0.65rem; border-radius: var(--brand-radius-pill, 999px); font-size: 0.7rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; }
            .badge-ok { background: rgba(182, 145, 88, 0.92); color: #FFF; }
            .badge-green { background: rgba(22, 163, 74, 0.92); color: #FFF; }
            .badge-out { background: rgba(24, 24, 27, 0.85); color: #FAF8F5; }
            .filter-bar select option { background: var(--brand-surface, #FFF); color: var(--brand-text, #18181B); }
            .card-body { padding: var(--brand-pad, 1.1rem); display: flex; flex-direction: column; flex: 1; gap: 0.35rem; }
            .cat { font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.14em; color: var(--brand-accent, #B69158); font-weight: 700; }
            .title { font-family: var(--brand-font-display, Georgia, serif); font-weight: 600; font-size: 1.08rem; color: var(--brand-text, #18181B); text-decoration: none; }
            .price { font-weight: 700; font-size: 1.04rem; }
            .empty-box { background: var(--brand-surface, #FFF); border: 1px dashed var(--brand-border, #D6D3D1); border-radius: var(--brand-radius-card, 8px); padding: 2.5rem; text-align: center; color: var(--brand-text-muted, #57534E); }
            .detail-wrap { padding: 2rem 1.25rem 3.5rem; display: grid; grid-template-columns: repeat(auto-fit, minmax(290px, 1fr)); gap: 2rem; align-items: start; }
            .detail-main-img { aspect-ratio: 4 / 5; background: var(--brand-surface-subtle, #F3EFE8); border-radius: var(--brand-radius-card, 10px); overflow: hidden; border: 1px solid var(--brand-border, #E7E2DA); }
            .detail-main-img img { width: 100%; height: 100%; object-fit: cover; display: block; }
            .thumbs { display: flex; gap: 0.55rem; overflow-x: auto; margin-top: 0.7rem; }
            .thumbs img { width: 68px; height: 86px; object-fit: cover; border-radius: var(--brand-radius-btn, 6px); cursor: pointer; border: 1.5px solid var(--brand-border, #D6D3D1); }
            .detail-panel { background: var(--brand-surface, #FFF); border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-card, 10px); padding: 1.65rem; }
            .chips { display: flex; gap: 0.45rem; flex-wrap: wrap; margin-top: 0.4rem; }
            .chip { background: var(--brand-bg, #FAF8F5); color: var(--brand-text, #18181B); border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-pill, 999px); padding: 0.48rem 0.9rem; font-weight: 600; cursor: pointer; }
            .btn-wa-big { display: block; width: 100%; background: #16A34A; color: #FFF; text-align: center; text-decoration: none; padding: 0.95rem; border-radius: var(--brand-radius-btn, 8px); font-weight: 700; font-size: 1rem; margin-top: 1.25rem; }
            .wa-preview { background: var(--brand-surface-subtle, #F3EFE8); border: 1px solid var(--brand-border, #E7E2DA); border-radius: var(--brand-radius-btn, 8px); padding: 0.9rem; margin-top: 0.9rem; font-size: 0.84rem; color: var(--brand-text-muted, #57534E); }
        </style>
        """;
    }

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
        (text ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("'", "\\u0027")
            .Replace("\"", "\\u0022")
            .Replace("<", "\\u003c")
            .Replace(">", "\\u003e")
            .Replace("&", "\\u0026")
            .Replace("/", "\\/")
            .Replace("\r", "")
            .Replace("\n", " ");

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
