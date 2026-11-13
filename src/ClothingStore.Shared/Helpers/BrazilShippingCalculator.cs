using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Regras gratuitas (sem CNPJ e sem API paga) para validação de distância de entrega por CEP,
/// consulta automática de CEP via BrasilAPI/ViaCEP com fallback offline e estimativa de frete Correios (PAC e SEDEX).
/// </summary>
public static class BrazilShippingCalculator
{
    public const string OfficialCorreiosUrl = "https://www2.correios.com.br/sistemas/precosPrazos/";

    public static class DeliveryScopeModes
    {
        public const string AllBrazil = "TodoBrasil";
        public const string SameState = "MesmoEstado";
        public const string SameCity = "MesmaCidade";
        public const string RadiusKm = "RaioKm";

        public static string Normalize(string? raw)
        {
            var token = (raw ?? string.Empty).Trim();
            if (string.Equals(token, SameState, StringComparison.OrdinalIgnoreCase)) return SameState;
            if (string.Equals(token, SameCity, StringComparison.OrdinalIgnoreCase)) return SameCity;
            if (string.Equals(token, RadiusKm, StringComparison.OrdinalIgnoreCase)) return RadiusKm;
            return AllBrazil;
        }

        public static string Describe(string? scopeMode, int maxDistanceKm, string? originCity, string? originState)
        {
            var scope = Normalize(scopeMode);
            var cityUf = !string.IsNullOrWhiteSpace(originCity) && !string.IsNullOrWhiteSpace(originState)
                ? $"{originCity}/{originState}"
                : !string.IsNullOrWhiteSpace(originState) ? originState : "sua região";

            return scope switch
            {
                SameCity when maxDistanceKm > 0 => $"Somente mesma cidade ({cityUf}) — até {maxDistanceKm} km",
                SameCity => $"Somente mesma cidade ({cityUf})",
                SameState when maxDistanceKm > 0 => $"Mesmo estado ({originState ?? "UF"}) — até {maxDistanceKm} km",
                SameState => $"Mesmo estado ({originState ?? "UF"})",
                RadiusKm => maxDistanceKm > 0 ? $"Até {maxDistanceKm} km de distância de {cityUf}" : $"Raio personalizado a partir de {cityUf}",
                _ when maxDistanceKm > 0 => $"Todo o Brasil (limite de até {maxDistanceKm} km)",
                _ => "Todo o Brasil"
            };
        }
    }

    public static class FulfillmentMethods
    {
        public const string Pending = "A definir pelo vendedor";
        public const string CorreiosPac = "Correios PAC";
        public const string CorreiosSedex = "Correios SEDEX";
        public const string LocalMotoboy = "Motoboy / Entrega Local";
        public const string Carrier = "Transportadora";
        public const string StorePickup = "Retirada Combinada";

        public static readonly string[] All =
        [
            Pending,
            CorreiosPac,
            CorreiosSedex,
            LocalMotoboy,
            Carrier,
            StorePickup
        ];
    }

    public static class OrderStatuses
    {
        public const string AwaitingPix = "AguardandoPix";
        public const string PaidPreparing = "PagoSeparandoEnvio";
        public const string Shipped = "Enviado";
        public const string Delivered = "Entregue";
        public const string Cancelled = "Cancelado";

        public static string LabelFor(string? status) => status switch
        {
            AwaitingPix => "Aguardando pagamento Pix",
            PaidPreparing => "Pix pago — Separando envio",
            Shipped => "Enviado para o cliente",
            Delivered => "Entregue",
            Cancelled => "Cancelado",
            _ => status ?? AwaitingPix
        };
    }

    public static bool TrySanitizeCep(string? rawCep, out string digits)
    {
        digits = Regex.Replace(rawCep ?? string.Empty, @"\D", "");
        return digits.Length == 8;
    }

    public static string SanitizeCepOrEmpty(string? rawCep) =>
        TrySanitizeCep(rawCep, out var digits) ? digits : string.Empty;

    public static string FormatCep(string? rawCep)
    {
        if (!TrySanitizeCep(rawCep, out var d))
            return (rawCep ?? string.Empty).Trim();

        return $"{d[..5]}-{d[5..]}";
    }

    public static double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return Math.Round(EarthRadiusKm * c, 1);
    }

    public static CepAddressLookupDto ResolveRegionFromCep(string rawCep)
    {
        if (!TrySanitizeCep(rawCep, out var cep))
        {
            throw new ArgumentException("Informe um CEP brasileiro válido com 8 dígitos (ex.: 01310-100).", nameof(rawCep));
        }

        var prefix3 = int.Parse(cep[..3], CultureInfo.InvariantCulture);
        var prefix2 = int.Parse(cep[..2], CultureInfo.InvariantCulture);

        var region = ResolvePrefixInfo(prefix2, prefix3);
        // Pequeno deslocamento determinístico pelo sufixo para diferenciar CEPs distintos na mesma cidade
        var fineOffsetLat = (int.Parse(cep[2..5], CultureInfo.InvariantCulture) % 20 - 10) * 0.0025;
        var fineOffsetLon = (int.Parse(cep[5..8], CultureInfo.InvariantCulture) % 20 - 10) * 0.0025;

        return new CepAddressLookupDto(
            FormatCep(cep),
            string.Empty,
            string.Empty,
            region.DefaultCity,
            region.StateUf,
            Math.Round(region.Latitude + fineOffsetLat, 5),
            Math.Round(region.Longitude + fineOffsetLon, 5),
            "Estimativa Regional Offline (CEP)");
    }

    public static async Task<CepAddressLookupDto> LookupCepAsync(
        string rawCep,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var fallback = ResolveRegionFromCep(rawCep);
        var digits = SanitizeCepOrEmpty(rawCep);

        if (httpClient is null)
            return fallback;

        // 1. BrasilAPI v2 (gratuita, sem chave, retorna coordenadas quando disponíveis)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(2200));

            using var resp = await httpClient.GetAsync($"https://brasilapi.com.br/api/cep/v2/{digits}", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
                var root = doc.RootElement;
                var city = root.TryGetProperty("city", out var cProp) ? cProp.GetString() : null;
                var state = root.TryGetProperty("state", out var sProp) ? sProp.GetString() : null;
                var street = root.TryGetProperty("street", out var stProp) ? stProp.GetString() : null;
                var neighborhood = root.TryGetProperty("neighborhood", out var nbProp) ? nbProp.GetString() : null;

                var lat = fallback.Latitude;
                var lon = fallback.Longitude;
                if (root.TryGetProperty("location", out var locProp) &&
                    locProp.TryGetProperty("coordinates", out var coordProp))
                {
                    if (coordProp.TryGetProperty("latitude", out var latProp) &&
                        double.TryParse(latProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat))
                    {
                        lat = parsedLat;
                    }
                    if (coordProp.TryGetProperty("longitude", out var lonProp) &&
                        double.TryParse(lonProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon))
                    {
                        lon = parsedLon;
                    }
                }

                if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
                {
                    return new CepAddressLookupDto(
                        FormatCep(digits),
                        street ?? string.Empty,
                        neighborhood ?? string.Empty,
                        city.Trim(),
                        state.Trim().ToUpperInvariant(),
                        lat,
                        lon,
                        "BrasilAPI (Online Grátis)");
                }
            }
        }
        catch
        {
        }

        // 2. Fallback gratuito ViaCEP
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(2200));

            using var resp = await httpClient.GetAsync($"https://viacep.com.br/ws/{digits}/json/", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
                var root = doc.RootElement;
                var hasError = root.TryGetProperty("erro", out var errProp) && errProp.ValueKind == JsonValueKind.True;
                if (!hasError)
                {
                    var city = root.TryGetProperty("localidade", out var cProp) ? cProp.GetString() : null;
                    var state = root.TryGetProperty("uf", out var sProp) ? sProp.GetString() : null;
                    var street = root.TryGetProperty("logradouro", out var stProp) ? stProp.GetString() : null;
                    var neighborhood = root.TryGetProperty("bairro", out var nbProp) ? nbProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
                    {
                        return new CepAddressLookupDto(
                            FormatCep(digits),
                            street ?? string.Empty,
                            neighborhood ?? string.Empty,
                            city.Trim(),
                            state.Trim().ToUpperInvariant(),
                            fallback.Latitude,
                            fallback.Longitude,
                            "ViaCEP (Online Grátis)");
                    }
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    public static DeliveryEligibilityDto EvaluateDeliveryEligibility(
        string? originCep,
        string? originCity,
        string? originState,
        string deliveryScopeMode,
        int maxDeliveryDistanceKm,
        string? destCep,
        string? destCity,
        string? destState,
        double? originLatOverride = null,
        double? originLonOverride = null,
        double? destLatOverride = null,
        double? destLonOverride = null)
    {
        if (!TrySanitizeCep(originCep, out var cleanOriginCep))
        {
            return new DeliveryEligibilityDto(
                false,
                0,
                originCity ?? string.Empty,
                originState ?? string.Empty,
                destCity ?? string.Empty,
                destState ?? string.Empty,
                "O vendedor ainda não configurou um CEP de origem válido para calcular a entrega.");
        }

        if (!TrySanitizeCep(destCep, out var cleanDestCep))
        {
            return new DeliveryEligibilityDto(
                false,
                0,
                originCity ?? string.Empty,
                originState ?? string.Empty,
                destCity ?? string.Empty,
                destState ?? string.Empty,
                "Informe um CEP de destino válido com 8 dígitos.");
        }

        var originInfo = ResolveRegionFromCep(cleanOriginCep);
        var destInfo = ResolveRegionFromCep(cleanDestCep);

        var effOriginCity = string.IsNullOrWhiteSpace(originCity) ? originInfo.City : originCity.Trim();
        var effOriginState = string.IsNullOrWhiteSpace(originState) ? originInfo.State : originState.Trim().ToUpperInvariant();
        var effDestCity = string.IsNullOrWhiteSpace(destCity) ? destInfo.City : destCity.Trim();
        var effDestState = string.IsNullOrWhiteSpace(destState) ? destInfo.State : destState.Trim().ToUpperInvariant();

        var lat1 = originLatOverride ?? originInfo.Latitude;
        var lon1 = originLonOverride ?? originInfo.Longitude;
        var lat2 = destLatOverride ?? destInfo.Latitude;
        var lon2 = destLonOverride ?? destInfo.Longitude;

        double distanceKm;
        if (cleanOriginCep == cleanDestCep)
        {
            distanceKm = 0.5;
        }
        else if (cleanOriginCep[..5] == cleanDestCep[..5])
        {
            distanceKm = Math.Max(1.0, CalculateDistanceKm(lat1, lon1, lat2, lon2));
        }
        else
        {
            distanceKm = Math.Max(2.0, CalculateDistanceKm(lat1, lon1, lat2, lon2));
        }

        var scope = DeliveryScopeModes.Normalize(deliveryScopeMode);
        var sameState = string.Equals(effOriginState, effDestState, StringComparison.OrdinalIgnoreCase);
        var sameCityName = !string.IsNullOrWhiteSpace(effOriginCity) && !string.IsNullOrWhiteSpace(effDestCity)
            ? string.Equals(effOriginCity, effDestCity, StringComparison.OrdinalIgnoreCase)
            : cleanOriginCep[..5] == cleanDestCep[..5];

        if (scope == DeliveryScopeModes.SameCity)
        {
            if (!sameState || (!sameCityName && distanceKm > 35.0))
            {
                return new DeliveryEligibilityDto(
                    false,
                    distanceKm,
                    effOriginCity,
                    effOriginState,
                    effDestCity,
                    effDestState,
                    $"Esta loja envia apenas para a cidade de {effOriginCity}/{effOriginState}. Seu CEP fica a aproximadamente {distanceKm:0.#} km ({effDestCity}/{effDestState}).");
            }
        }
        else if (scope == DeliveryScopeModes.SameState)
        {
            if (!sameState)
            {
                return new DeliveryEligibilityDto(
                    false,
                    distanceKm,
                    effOriginCity,
                    effOriginState,
                    effDestCity,
                    effDestState,
                    $"Esta loja realiza vendas pelo app apenas dentro do estado {effOriginState}. Seu CEP pertence a {effDestState} (~{distanceKm:0.#} km).");
            }
        }
        else if (scope == DeliveryScopeModes.RadiusKm)
        {
            var limitKm = maxDeliveryDistanceKm > 0 ? maxDeliveryDistanceKm : 50;
            if (distanceKm > limitKm)
            {
                return new DeliveryEligibilityDto(
                    false,
                    distanceKm,
                    effOriginCity,
                    effOriginState,
                    effDestCity,
                    effDestState,
                    $"Distância aproximada de {distanceKm:0.#} km excede o raio máximo configurado pelo vendedor ({limitKm} km a partir de {effOriginCity}/{effOriginState}).");
            }
        }

        if (maxDeliveryDistanceKm > 0 && distanceKm > maxDeliveryDistanceKm)
        {
            return new DeliveryEligibilityDto(
                false,
                distanceKm,
                effOriginCity,
                effOriginState,
                effDestCity,
                effDestState,
                $"Distância aproximada de {distanceKm:0.#} km ultrapassa o limite máximo de {maxDeliveryDistanceKm} km definido pelo vendedor.");
        }

        return new DeliveryEligibilityDto(
            true,
            distanceKm,
            effOriginCity,
            effOriginState,
            effDestCity,
            effDestState,
            $"Endereço dentro da área de entrega do vendedor (~{distanceKm:0.#} km de {effOriginCity}/{effOriginState}).");
    }

    /// <summary>
    /// Calcula estimativa realista de balcão dos Correios (PAC e SEDEX sem contrato CNPJ)
    /// com base no CEP de origem/destino, peso real, peso cúbico e faixa tarifária nacional.
    /// </summary>
    public static CorreiosQuoteDto EstimateCorreios(
        string originCep,
        string destCep,
        int weightGrams = 450,
        int lengthCm = 24,
        int widthCm = 18,
        int heightCm = 8,
        string? originStateOverride = null,
        string? destStateOverride = null,
        double? distanceKmOverride = null)
    {
        var origin = ResolveRegionFromCep(originCep);
        var dest = ResolveRegionFromCep(destCep);

        var origUf = string.IsNullOrWhiteSpace(originStateOverride) ? origin.State : originStateOverride.Trim().ToUpperInvariant();
        var destUf = string.IsNullOrWhiteSpace(destStateOverride) ? dest.State : destStateOverride.Trim().ToUpperInvariant();

        var distanceKm = distanceKmOverride
            ?? (SanitizeCepOrEmpty(originCep) == SanitizeCepOrEmpty(destCep)
                ? 0.5
                : Math.Max(2.0, CalculateDistanceKm(origin.Latitude, origin.Longitude, dest.Latitude, dest.Longitude)));

        var effWeightG = Math.Clamp(weightGrams <= 0 ? 450 : weightGrams, 100, 30000);
        var effL = Math.Clamp(lengthCm <= 0 ? 24 : lengthCm, 16, 100);
        var effW = Math.Clamp(widthCm <= 0 ? 18 : widthCm, 11, 100);
        var effH = Math.Clamp(heightCm <= 0 ? 8 : heightCm, 2, 100);

        // Regra padrão dos Correios: peso cúbico (C x L x A) / 6000; acima de 5 kg prevalece o maior peso
        var cubicWeightGrams = (int)Math.Ceiling((effL * effW * effH) / 6000.0 * 1000.0);
        var billableWeightGrams = cubicWeightGrams > 5000
            ? Math.Clamp(Math.Max(effWeightG, cubicWeightGrams), 300, 30000)
            : Math.Clamp(effWeightG, 300, 30000);

        var sameState = string.Equals(origUf, destUf, StringComparison.OrdinalIgnoreCase);

        string zoneName;
        decimal pacBase;
        decimal pacPerExtraKg;
        int pacDaysMin;
        int pacDaysMax;
        decimal sedexBase;
        decimal sedexPerExtraKg;
        int sedexDaysMin;
        int sedexDaysMax;

        if (distanceKm <= 50.0 && sameState)
        {
            zoneName = "Local / Metropolitana";
            pacBase = 21.80m; pacPerExtraKg = 3.10m; pacDaysMin = 3; pacDaysMax = 5;
            sedexBase = 24.90m; sedexPerExtraKg = 3.90m; sedexDaysMin = 1; sedexDaysMax = 2;
        }
        else if (sameState)
        {
            zoneName = $"Estadual ({origUf})";
            pacBase = 24.40m; pacPerExtraKg = 4.20m; pacDaysMin = 4; pacDaysMax = 6;
            sedexBase = 31.50m; sedexPerExtraKg = 5.80m; sedexDaysMin = 2; sedexDaysMax = 3;
        }
        else if (distanceKm <= 650.0)
        {
            zoneName = $"Regional ({origUf} → {destUf})";
            pacBase = 27.90m; pacPerExtraKg = 5.60m; pacDaysMin = 5; pacDaysMax = 8;
            sedexBase = 44.80m; sedexPerExtraKg = 9.40m; sedexDaysMin = 2; sedexDaysMax = 4;
        }
        else if (distanceKm <= 1600.0)
        {
            zoneName = $"Nacional ({origUf} → {destUf})";
            pacBase = 33.40m; pacPerExtraKg = 7.90m; pacDaysMin = 6; pacDaysMax = 10;
            sedexBase = 62.90m; sedexPerExtraKg = 14.50m; sedexDaysMin = 3; sedexDaysMax = 5;
        }
        else
        {
            zoneName = $"Longa Distância ({origUf} → {destUf})";
            pacBase = 41.20m; pacPerExtraKg = 11.40m; pacDaysMin = 9; pacDaysMax = 15;
            sedexBase = 84.50m; sedexPerExtraKg = 21.20m; sedexDaysMin = 4; sedexDaysMax = 7;
        }

        // Adicional por faixa de peso cobrado acima de 500 g
        var extraKgUnits = billableWeightGrams <= 500
            ? 0m
            : (decimal)Math.Ceiling((billableWeightGrams - 500) / 1000.0);

        var pacPrice = Math.Round(pacBase + extraKgUnits * pacPerExtraKg, 2);
        var sedexPrice = Math.Round(sedexBase + extraKgUnits * sedexPerExtraKg, 2);

        return new CorreiosQuoteDto(
            FormatCep(originCep),
            FormatCep(destCep),
            Math.Round(distanceKm, 1),
            zoneName,
            effWeightG,
            cubicWeightGrams,
            billableWeightGrams,
            pacPrice,
            pacDaysMin,
            pacDaysMax,
            sedexPrice,
            sedexDaysMin,
            sedexDaysMax,
            OfficialCorreiosUrl);
    }

    private static double ToRadians(double deg) => deg * Math.PI / 180.0;

    private static (string DefaultCity, string StateUf, double Latitude, double Longitude) ResolvePrefixInfo(int prefix2, int prefix3)
    {
        // Faixas específicas de 3 dígitos que compartilham prefixo de 2 dígitos
        if (prefix3 == 689) return ("Macapá", "AP", 0.0349, -51.0694);
        if (prefix3 == 693) return ("Boa Vista", "RR", 2.8235, -60.6758);
        if (prefix3 == 699) return ("Rio Branco", "AC", -9.9754, -67.8249);
        if (prefix3 is 728 or 729) return ("Luziânia / Entorno DF", "GO", -16.2533, -47.9500);
        if (prefix3 is 768 or 769) return ("Porto Velho", "RO", -8.7612, -63.9004);

        return prefix2 switch
        {
            >= 1 and <= 5 or 8 => ("São Paulo", "SP", -23.5505, -46.6333),
            6 => ("Osasco / Barueri", "SP", -23.5325, -46.7917),
            7 => ("Guarulhos", "SP", -23.4543, -46.5337),
            9 => ("Santo André / ABC", "SP", -23.6639, -46.5383),
            11 => ("Santos", "SP", -23.9608, -46.3336),
            12 => ("São José dos Campos", "SP", -23.1896, -45.8841),
            13 => ("Campinas", "SP", -22.9099, -47.0626),
            14 => ("Ribeirão Preto", "SP", -21.1704, -47.8103),
            15 => ("São José do Rio Preto", "SP", -20.8197, -49.3794),
            16 => ("Araçatuba", "SP", -21.2076, -50.4329),
            17 => ("Bauru", "SP", -22.3145, -49.0587),
            18 => ("Sorocaba", "SP", -23.5015, -47.4526),
            19 => ("Presidente Prudente", "SP", -22.1256, -51.3889),

            >= 20 and <= 23 => ("Rio de Janeiro", "RJ", -22.9068, -43.1729),
            24 or 25 => ("Niterói / Petrópolis", "RJ", -22.8833, -43.1036),
            26 or 27 => ("Volta Redonda / Baixada", "RJ", -22.5231, -44.1044),
            28 => ("Campos dos Goytacazes", "RJ", -21.7545, -41.3244),
            29 => ("Vitória", "ES", -20.3155, -40.3128),

            >= 30 and <= 34 => ("Belo Horizonte", "MG", -19.9167, -43.9345),
            35 => ("Ipatinga", "MG", -19.4689, -42.5369),
            36 => ("Juiz de Fora", "MG", -21.7642, -43.3503),
            37 => ("Poços de Caldas", "MG", -21.7878, -46.5614),
            38 => ("Uberlândia", "MG", -18.9186, -48.2772),
            39 => ("Montes Claros", "MG", -16.7282, -43.8638),

            >= 40 and <= 42 => ("Salvador", "BA", -12.9714, -38.5014),
            43 or 44 => ("Feira de Santana", "BA", -12.2664, -38.9663),
            >= 45 and <= 48 => ("Vitória da Conquista / Interior", "BA", -14.8619, -40.8442),
            49 => ("Aracaju", "SE", -10.9472, -37.0731),

            >= 50 and <= 54 => ("Recife", "PE", -8.0476, -34.8770),
            55 or 56 => ("Caruaru / Petrolina", "PE", -8.2833, -35.9761),
            57 => ("Maceió", "AL", -9.6498, -35.7089),
            58 => ("João Pessoa", "PB", -7.1195, -34.8450),
            59 => ("Natal", "RN", -5.7793, -35.2009),

            60 or 61 => ("Fortaleza", "CE", -3.7319, -38.5267),
            62 or 63 => ("Sobral / Juazeiro do Norte", "CE", -7.2131, -39.3153),
            64 => ("Teresina", "PI", -5.0892, -42.8016),
            65 => ("São Luís", "MA", -2.5307, -44.3068),
            >= 66 and <= 68 => ("Belém", "PA", -1.4558, -48.4902),
            69 => ("Manaus", "AM", -3.1190, -60.0217),

            >= 70 and <= 73 => ("Brasília", "DF", -15.7975, -47.8919),
            >= 74 and <= 76 => ("Goiânia", "GO", -16.6869, -49.2648),
            77 => ("Palmas", "TO", -10.1840, -48.3336),
            78 => ("Cuiabá", "MT", -15.6014, -56.0979),
            79 => ("Campo Grande", "MS", -20.4697, -54.6201),

            >= 80 and <= 83 => ("Curitiba", "PR", -25.4284, -49.2733),
            >= 84 and <= 87 => ("Londrina / Maringá", "PR", -23.3045, -51.1696),
            88 => ("Florianópolis", "SC", -27.5954, -48.5480),
            89 => ("Joinville / Blumenau", "SC", -26.3044, -48.8487),

            >= 90 and <= 94 => ("Porto Alegre", "RS", -30.0346, -51.2177),
            >= 95 and <= 99 => ("Caxias do Sul / Interior", "RS", -29.1678, -51.1794),

            _ => ("São Paulo", "SP", -23.5505, -46.6333)
        };
    }
}
