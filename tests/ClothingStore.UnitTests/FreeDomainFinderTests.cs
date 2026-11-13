using System.Net;
using System.Text;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class FreeDomainFinderTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
                RequestedUris.Add(request.RequestUri);

            return Task.FromResult(responder(request));
        }
    }

    [TestMethod]
    public void NormalizeDomainSlug_ShouldSanitizeAccentsProtocolsAndPrefixes()
    {
        Assert.AreEqual("espaco-urbano", FreeDomainFinder.NormalizeDomainSlug("Espaço Urbano!"));
        Assert.AreEqual("minhaloja", FreeDomainFinder.NormalizeDomainSlug("https://lojas.minhaloja.com/produtos"));
        Assert.AreEqual("ateliemae", FreeDomainFinder.NormalizeDomainSlug("AteliêMãe.duckdns.org"));
        Assert.AreEqual("minhaloja", FreeDomainFinder.NormalizeDomainSlug("   "));
    }

    [TestMethod]
    public void CalculateCheaperPlanWithFreeDomain_ShouldApplyMonthlySavings()
    {
        var (basicoFinal, basicoSave) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(49.90m);
        Assert.AreEqual(39.90m, basicoFinal);
        Assert.AreEqual(10.00m, basicoSave);

        var (meiaFinal, meiaSave) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(89.90m);
        Assert.AreEqual(79.90m, meiaFinal);
        Assert.AreEqual(10.00m, meiaSave);

        var (premiumFinal, premiumSave) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(129.90m);
        Assert.AreEqual(119.90m, premiumFinal);
        Assert.AreEqual(10.00m, premiumSave);

        var (lowFinal, lowSave) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(29.90m);
        Assert.AreEqual(25.41m, lowFinal);
        Assert.AreEqual(4.49m, lowSave);

        var (zeroFinal, zeroSave) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(0m);
        Assert.AreEqual(0m, zeroFinal);
        Assert.AreEqual(0m, zeroSave);
    }

    [TestMethod]
    public async Task SearchAvailableFreeDomainsAsync_ShouldHighlightAvailableDomainWithCheaperPlanOffer()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Status":3}""", Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);

        var result = await FreeDomainFinder.SearchAvailableFreeDomainsAsync("Moda Urbana", client);

        Assert.AreEqual("moda-urbana", result.QuerySlug);
        Assert.IsNotNull(result.FeaturedOffer);
        Assert.IsTrue(result.FeaturedOffer!.IsAvailable);
        Assert.IsTrue(result.FeaturedOffer.VerifiedLiveDns);
        Assert.AreEqual(0m, result.FeaturedOffer.ExtraDomainCostPerYear);
        Assert.IsTrue(
            result.FeaturedOffer.OfferHeadline.Contains("disponível", StringComparison.OrdinalIgnoreCase) &&
            result.FeaturedOffer.OfferHeadline.Contains("plano mais barato", StringComparison.OrdinalIgnoreCase),
            $"Headline inesperada: {result.FeaturedOffer.OfferHeadline}");
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "modaurbana.duckdns.org" && s.IsAvailable));
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "moda-urbana.fly.dev" && s.IsAvailable));
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "lojas.modaurbana.com" && s.IsAvailable && s.RegistrationMode == "InstantIncluded"));
    }

    [TestMethod]
    public async Task SearchAvailableFreeDomainsAsync_ShouldFallbackToCloudflareDoh_WhenGoogleDnsFails()
    {
        using var handler = new StubHttpMessageHandler(req =>
        {
            var host = req.RequestUri?.Host ?? string.Empty;
            if (host.Contains("dns.google", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Status":3}""", Encoding.UTF8, "application/dns-json")
            };
        });
        using var client = new HttpClient(handler);

        var result = await FreeDomainFinder.SearchAvailableFreeDomainsAsync("Loja Fallback", client);

        Assert.IsNotNull(result.FeaturedOffer);
        Assert.IsTrue(result.FeaturedOffer!.IsAvailable);
        Assert.IsTrue(result.FeaturedOffer.VerifiedLiveDns);
        Assert.IsTrue(handler.RequestedUris.Any(u => u.Host.Contains("cloudflare-dns.com", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SearchAvailableFreeDomainsAsync_ShouldGenerateBackupsWhenPrimaryTaken_AndSupportOwnerWildcard()
    {
        using var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            var isBackup = url.Contains("auramodaloja.duckdns.org") ||
                           url.Contains("useauramoda.dpdns.org") ||
                           url.Contains("aura-moda-loja.pages.dev") ||
                           url.Contains("auramoda.meuhub.com.br");

            var status = isBackup ? 3 : 0;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"Status\":{status}}}", Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);

        var result = await FreeDomainFinder.SearchAvailableFreeDomainsAsync(
            "Aura Moda",
            client,
            ownerCustomRootDomain: "https://meuhub.com.br/");

        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "auramoda.meuhub.com.br" && s.IsAvailable));
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "auramodaloja.duckdns.org" && s.IsAvailable));
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "useauramoda.dpdns.org" && s.IsAvailable));
        Assert.IsTrue(result.Candidates.Any(s => s.Domain == "auramoda.duckdns.org" && !s.IsAvailable));
    }

    [TestMethod]
    public async Task UpdateDuckDnsIpAsync_ShouldValidateInputsAndHandleApiResponses()
    {
        using var okHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("OK", Encoding.UTF8, "text/plain")
            });
        using var okClient = new HttpClient(okHandler);

        var missingSub = await FreeDomainFinder.UpdateDuckDnsIpAsync("", "abc", null, okClient);
        Assert.IsFalse(missingSub.Success);

        var missingToken = await FreeDomainFinder.UpdateDuckDnsIpAsync("minhaloja.duckdns.org", "", null, okClient);
        Assert.IsFalse(missingToken.Success);

        var success = await FreeDomainFinder.UpdateDuckDnsIpAsync("https://minhaloja.duckdns.org", "tok-123", "187.10.20.30", okClient);
        Assert.IsTrue(success.Success);
        Assert.IsTrue(okHandler.RequestedUris.Any(u =>
            u.ToString().Contains("domains=minhaloja", StringComparison.Ordinal) &&
            u.ToString().Contains("token=tok-123", StringComparison.Ordinal) &&
            u.ToString().Contains("ip=187.10.20.30", StringComparison.Ordinal)));

        using var koHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("KO", Encoding.UTF8, "text/plain")
            });
        using var koClient = new HttpClient(koHandler);

        var fail = await FreeDomainFinder.UpdateDuckDnsIpAsync("minhaloja", "bad-tok", null, koClient);
        Assert.IsFalse(fail.Success);
    }
}
