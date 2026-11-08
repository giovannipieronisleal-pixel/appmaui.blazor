using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço de controle de estoque (Entradas, Saídas, Ajustes), alertas de estoque baixo e indicadores do Dashboard.
/// </summary>
public sealed class StockManagementService
{
    private readonly IClothingStoreRepository _repository;

    public StockManagementService(IClothingStoreRepository repository)
    {
        _repository = repository;
    }

    public async Task<StockMovementDto> RegisterMovementAsync(
        Guid employeeId,
        CreateStockMovementRequest request,
        CancellationToken ct = default)
    {
        var employee = await _repository.GetEmployeeByIdAsync(employeeId, ct)
            ?? throw new DomainException("Funcionário responsável não encontrado.");

        if (!employee.IsActive)
            throw new DomainException("Funcionário inativo não pode registrar movimentações de estoque.");

        var variant = await _repository.GetVariantByIdAsync(request.ProductVariantId, ct)
            ?? throw new DomainException("Variante de produto não encontrada.");

        var product = await _repository.GetProductByIdAsync(variant.ProductId, ct)
            ?? throw new DomainException("Produto associado à variante não encontrado.");

        var movementType = ParseMovementType(request.MovementType);
        var movement = variant.ApplyStockMovement(employeeId, movementType, request.Quantity, request.Reason);

        await _repository.RecordStockMovementAsync(variant, movement, ct);
        await _repository.UpdateProductAsync(product, ct);

        return new StockMovementDto(
            movement.Id,
            movement.ProductVariantId,
            product.Id,
            product.Name,
            variant.Sku,
            variant.Color,
            variant.Size,
            employee.Id,
            employee.FullName,
            movement.MovementType.ToString().ToUpperInvariant(),
            movement.QuantityChanged,
            movement.PreviousQuantity,
            movement.NewQuantity,
            movement.Reason,
            movement.CreatedAt);
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        Guid? employeeId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var movements = await _repository.GetStockMovementsAsync(productId, variantId, employeeId, limit, ct);
        var products = await _repository.GetProductsAsync(includeArchived: true, ct);
        var employees = (await _repository.GetEmployeesAsync(ct)).ToDictionary(e => e.Id);

        var variantLookup = products
            .SelectMany(p => p.Variants.Select(v => (Product: p, Variant: v)))
            .ToDictionary(x => x.Variant.Id);

        return movements
            .Select(m =>
            {
                variantLookup.TryGetValue(m.ProductVariantId, out var pv);
                employees.TryGetValue(m.EmployeeId, out var emp);

                return new StockMovementDto(
                    m.Id,
                    m.ProductVariantId,
                    pv.Product?.Id ?? Guid.Empty,
                    pv.Product?.Name ?? "Produto Removido",
                    pv.Variant?.Sku ?? "-",
                    pv.Variant?.Color ?? "-",
                    pv.Variant?.Size ?? "-",
                    m.EmployeeId,
                    emp?.FullName ?? "Funcionário",
                    m.MovementType.ToString().ToUpperInvariant(),
                    m.QuantityChanged,
                    m.PreviousQuantity,
                    m.NewQuantity,
                    m.Reason,
                    m.CreatedAt);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<StockAlertItemDto>> GetStockAlertsAsync(CancellationToken ct = default)
    {
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);

        return products
            .SelectMany(p => p.Variants
                .Where(v => v.IsActive && (v.IsOutOfStock || v.IsLowStock))
                .Select(v => new StockAlertItemDto(
                    v.Id,
                    p.Id,
                    p.Name,
                    v.Sku,
                    v.Color,
                    v.ColorHex ?? ProductVariant.ResolveDefaultColorHex(v.Color),
                    v.Size,
                    v.StockQuantity,
                    v.LowStockThreshold,
                    v.IsOutOfStock)))
            .OrderBy(a => a.StockQuantity)
            .ThenBy(a => a.ProductName)
            .ToList();
    }

    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var activeVariants = products.SelectMany(p => p.Variants.Where(v => v.IsActive)).ToList();

        var totalProducts = products.Count;
        var visibleProducts = products.Count(p => p.IsVisibleOnSite);
        var totalUnits = activeVariants.Sum(v => v.StockQuantity);
        var lowStockCount = activeVariants.Count(v => v.IsLowStock);
        var outOfStockCount = activeVariants.Count(v => v.IsOutOfStock);

        var alerts = await GetStockAlertsAsync(ct);
        var recentMovements = await GetMovementsAsync(limit: 12, ct: ct);

        return new DashboardSummaryDto(
            totalProducts,
            visibleProducts,
            totalUnits,
            lowStockCount,
            outOfStockCount,
            alerts,
            recentMovements);
    }

    private static StockMovementType ParseMovementType(string rawType)
    {
        return rawType?.Trim().ToUpperInvariant() switch
        {
            "IN" or "ENTRADA" => StockMovementType.In,
            "OUT" or "SAIDA" or "SAÍDA" => StockMovementType.Out,
            "ADJUSTMENT" or "AJUSTE" => StockMovementType.Adjustment,
            _ => throw new DomainException($"Tipo de movimentação inválido: '{rawType}'. Use IN, OUT ou ADJUSTMENT.")
        };
    }
}
