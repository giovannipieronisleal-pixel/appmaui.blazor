using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class DashboardCompactSalesTests
{
    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    [TestMethod]
    public void BuildCompactSalesFeed_ShouldExcludeStockInAndAdjustments_AndShowOnlyWhatSoldAndWho()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new DashboardSummaryDto(
            TotalRegisteredProducts: 4,
            TotalVisibleProducts: 4,
            TotalUnitsInStock: 30,
            LowStockVariantsCount: 0,
            OutOfStockVariantsCount: 0,
            StockAlerts: [],
            RecentMovements:
            [
                new StockMovementDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Camisa Tricot Modal Nóbrega", "SKU1", "Branco Puro", "M", Guid.NewGuid(), "giovanni", "IN", 7, 0, 7, "Estoque inicial", now.AddMinutes(-10)),
                new StockMovementDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Conjunto Pantalona Seda", "SKU2", "Champanhe", "P", Guid.NewGuid(), "giovanni", "ADJUSTMENT", 1, 3, 4, "Ajuste", now.AddMinutes(-8)),
                new StockMovementDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Camisa Tricot Modal Nóbrega", "SKU1", "Branco Puro", "M", Guid.NewGuid(), "vendedora.ana", "OUT", 2, 7, 5, "Venda #YLK-2026-001 (Pix) — Maria Clara", now.AddMinutes(-5)),
                new StockMovementDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Blazer Linho", "SKU3", "Preto", "G", Guid.NewGuid(), "giovanni", "OUT", 1, 5, 4, "Venda balcão — Carlos Souza", now.AddMinutes(-2))
            ]);

        var orderItem = new StoreOrderItemDto(Guid.NewGuid(), Guid.NewGuid(), "Camisa Tricot Modal Nóbrega", "SKU1", "Branco Puro", "M", 2, 199.90m, 399.80m);
        var confirmedOrder = new StoreOrderDto(
            Guid.NewGuid(), "YLK-2026-001", "Pix", "Confirmado", 399.80m, 25m, 40m, null, 399.80m,
            "Maria Clara", "11999998888", "01001000", "Rua A", "10", null, "Centro", "São Paulo", "SP",
            "Rua A, 10", null, 2.0, "Mesma cidade", 1, 3, 1, 2, "pix@teste.com", "000201",
            "Correios", null, null, true, now.AddMinutes(-6), now.AddMinutes(-5), null, null, null,
            [orderItem]);

        var salesReport = new SalesReportDto(
            true, "Premium", "", "30d", 399.80m, 399.80m, 399.80m, 399.80m, 399.80m, 0m, 0m,
            1, 1, 0, 0, 2, 399.80m, [], [], [confirmedOrder]);

        var feed = DashboardRecentSalesHelper.BuildCompactSalesFeed(summary, salesReport, maxItems: 5);

        Assert.AreEqual(2, feed.Count, "Apenas vendas/saídas devem aparecer (sem duplicar o mesmo pedido e sem IN/ADJUSTMENT).");
        Assert.AreEqual("1x Blazer Linho", feed[0].WhatSold);
        Assert.AreEqual("Carlos Souza", feed[0].Who);
        Assert.AreEqual("2x Camisa Tricot Modal Nóbrega", feed[1].WhatSold);
        Assert.AreEqual("Maria Clara", feed[1].Who);
    }

    [TestMethod]
    public void BuildCompactSalesFeed_ShouldFallbackToEmployeeName_WhenDirectStockOutHasNoCustomer_AndRespectLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var movements = Enumerable.Range(1, 8)
            .Select(i => new StockMovementDto(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                $"Peça {i}", $"SKU{i}", "Preto", "M",
                Guid.NewGuid(), $"Operador {i}", "OUT", 1, 5, 4,
                null, now.AddMinutes(-i)))
            .ToList();

        var summary = new DashboardSummaryDto(8, 8, 32, 0, 0, [], movements);
        var lockedReport = new SalesReportDto(
            false, "Basico", "Bloqueado", "30d", 0m, 0m, 0m, 0m, 0m, 0m, 0m,
            0, 0, 0, 0, 0, 0m, [], [], []);

        var feed = DashboardRecentSalesHelper.BuildCompactSalesFeed(summary, lockedReport);

        Assert.AreEqual(5, feed.Count, "O card do dashboard deve limitar a no máximo 5 itens compactos.");
        Assert.AreEqual("1x Peça 1", feed[0].WhatSold);
        Assert.AreEqual("Operador 1", feed[0].Who);
    }

    [TestMethod]
    public void HomeDashboard_ShouldRenderCompactSalesCard_WithoutOverflowingFiveColumnMovementsTable()
    {
        var root = ResolveRepoRoot();
        var homeRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Home.razor"));
        var homeCss = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Home.razor.css"));
        var appCss = File.ReadAllText(Path.Combine(root, "wwwroot", "app.css"));

        Assert.IsFalse(homeRazor.Contains("Últimas Movimentações", StringComparison.Ordinal),
            "A tabela larga de Últimas Movimentações não deve mais existir no dashboard.");
        Assert.IsFalse(homeRazor.Contains("data-label=\"Responsável\"", StringComparison.Ordinal));
        Assert.IsTrue(homeRazor.Contains("Últimas Vendas", StringComparison.Ordinal));
        Assert.IsTrue(homeRazor.Contains("adm-sales-mini-list", StringComparison.Ordinal));
        Assert.IsTrue(homeRazor.Contains("adm-table-wrap", StringComparison.Ordinal));
        Assert.IsTrue(homeCss.Contains(".adm-sales-mini-item", StringComparison.Ordinal));
        Assert.IsTrue(appCss.Contains("min-width: 0;", StringComparison.Ordinal));
    }
}
