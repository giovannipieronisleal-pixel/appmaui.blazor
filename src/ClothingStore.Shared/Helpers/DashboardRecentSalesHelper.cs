using System.Text.RegularExpressions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

public sealed record DashboardCompactSaleItem(
    string WhatSold,
    string Who);

/// <summary>
/// Monta o resumo compacto do dashboard contendo exclusivamente o que foi vendido e quem foi.
/// </summary>
public static partial class DashboardRecentSalesHelper
{
    private const int DefaultMaxItems = 5;

    [GeneratedRegex(@"#(?<code>[A-Z0-9-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrderNumberRegex();

    public static IReadOnlyList<DashboardCompactSaleItem> BuildCompactSalesFeed(
        DashboardSummaryDto? summary,
        SalesReportDto? salesReport,
        int maxItems = DefaultMaxItems)
    {
        if (maxItems <= 0)
        {
            return [];
        }

        var entries = new List<(DateTimeOffset At, DashboardCompactSaleItem Item)>();
        var representedOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (salesReport?.IsUnlocked == true && salesReport.RecentConfirmedSales is { Count: > 0 })
        {
            foreach (var order in salesReport.RecentConfirmedSales)
            {
                if (!string.IsNullOrWhiteSpace(order.OrderNumber))
                {
                    representedOrders.Add(order.OrderNumber.Trim());
                }

                var whatSold = FormatOrderItemsSummary(order);
                var who = string.IsNullOrWhiteSpace(order.CustomerName)
                    ? "Cliente"
                    : order.CustomerName.Trim();
                var timestamp = order.PixConfirmedAt ?? order.DeliveredAt ?? order.CreatedAt;

                entries.Add((timestamp, new DashboardCompactSaleItem(whatSold, who)));
            }
        }

        if (summary?.RecentMovements is { Count: > 0 })
        {
            foreach (var mov in summary.RecentMovements)
            {
                if (!string.Equals(mov.MovementType, "OUT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (MatchesRepresentedOrder(mov.Reason, representedOrders))
                {
                    continue;
                }

                var qty = Math.Max(1, Math.Abs(mov.QuantityChanged));
                var productName = string.IsNullOrWhiteSpace(mov.ProductName) ? "Peça" : mov.ProductName.Trim();
                var whatSold = $"{qty}x {productName}";
                var who = ResolveMovementWho(mov);

                entries.Add((mov.CreatedAt, new DashboardCompactSaleItem(whatSold, who)));
            }
        }

        return entries
            .OrderByDescending(e => e.At)
            .Take(maxItems)
            .Select(e => e.Item)
            .ToList();
    }

    private static string FormatOrderItemsSummary(StoreOrderDto order)
    {
        if (order.Items is null || order.Items.Count == 0)
        {
            return string.IsNullOrWhiteSpace(order.OrderNumber) ? "Venda registrada" : order.OrderNumber.Trim();
        }

        var first = order.Items[0];
        var qty = Math.Max(1, first.Quantity);
        var productName = string.IsNullOrWhiteSpace(first.ProductName) ? "Peça" : first.ProductName.Trim();

        return order.Items.Count == 1
            ? $"{qty}x {productName}"
            : $"{qty}x {productName} (+{order.Items.Count - 1})";
    }

    private static bool MatchesRepresentedOrder(string? reason, HashSet<string> representedOrders)
    {
        if (string.IsNullOrWhiteSpace(reason) || representedOrders.Count == 0)
        {
            return false;
        }

        var match = OrderNumberRegex().Match(reason);
        return match.Success && representedOrders.Contains(match.Groups["code"].Value.Trim());
    }

    private static string ResolveMovementWho(StockMovementDto mov)
    {
        if (!string.IsNullOrWhiteSpace(mov.Reason))
        {
            var dashIndex = mov.Reason.LastIndexOf(" — ", StringComparison.Ordinal);
            if (dashIndex >= 0 && dashIndex + 3 < mov.Reason.Length)
            {
                var customer = mov.Reason[(dashIndex + 3)..].Trim();
                if (!string.IsNullOrWhiteSpace(customer))
                {
                    return customer;
                }
            }
        }

        return string.IsNullOrWhiteSpace(mov.EmployeeName)
            ? "Equipe"
            : mov.EmployeeName.Trim();
    }
}
