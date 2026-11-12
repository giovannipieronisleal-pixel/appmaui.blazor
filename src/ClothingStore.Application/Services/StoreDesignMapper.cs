using ClothingStore.Domain.Entities;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

public static class StoreDesignMapper
{
    public static StoreDesignDto ToDto(StoreDesignConfig? source)
    {
        var c = (source ?? StoreDesignConfig.CreateMinimalistaLuxo()).CreateNormalizedCopy();
        return new StoreDesignDto
        {
            PresetKey = c.PresetKey,
            BackgroundColor = c.BackgroundColor,
            SurfaceColor = c.SurfaceColor,
            SurfaceSubtleColor = c.SurfaceSubtleColor,
            PrimaryColor = c.PrimaryColor,
            PrimaryTextColor = c.PrimaryTextColor,
            AccentColor = c.AccentColor,
            TextColor = c.TextColor,
            MutedTextColor = c.MutedTextColor,
            BorderColor = c.BorderColor,
            SidebarColor = c.SidebarColor,
            DisplayFont = c.DisplayFont,
            BodyFont = c.BodyFont,
            CornerRadiusMode = c.CornerRadiusMode,
            DensityMode = c.DensityMode,
            HeroLayout = c.HeroLayout,
            GridColumns = c.GridColumns,
            ShowLookbookBadge = c.ShowLookbookBadge,
            HeroEyebrow = c.HeroEyebrow,
            HeroHeadline = c.HeroHeadline
        };
    }

    public static StoreDesignConfig ToDomain(StoreDesignDto? source)
    {
        if (source is null)
            return StoreDesignConfig.CreateMinimalistaLuxo();

        return new StoreDesignConfig
        {
            PresetKey = source.PresetKey,
            BackgroundColor = source.BackgroundColor,
            SurfaceColor = source.SurfaceColor,
            SurfaceSubtleColor = source.SurfaceSubtleColor,
            PrimaryColor = source.PrimaryColor,
            PrimaryTextColor = source.PrimaryTextColor,
            AccentColor = source.AccentColor,
            TextColor = source.TextColor,
            MutedTextColor = source.MutedTextColor,
            BorderColor = source.BorderColor,
            SidebarColor = source.SidebarColor,
            DisplayFont = source.DisplayFont,
            BodyFont = source.BodyFont,
            CornerRadiusMode = source.CornerRadiusMode,
            DensityMode = source.DensityMode,
            HeroLayout = source.HeroLayout,
            GridColumns = source.GridColumns,
            ShowLookbookBadge = source.ShowLookbookBadge,
            HeroEyebrow = source.HeroEyebrow,
            HeroHeadline = source.HeroHeadline
        }.CreateNormalizedCopy();
    }
}
