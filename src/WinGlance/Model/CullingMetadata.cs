using System;

namespace WinGlance.Model;

public enum PickStatus
{
    Unflagged = 0,
    Pick = 1,
    Reject = -1
}

public enum ColorLabel
{
    None = 0,
    Red = 6,
    Yellow = 7,
    Green = 8,
    Blue = 9
}

public static class ColorLabelExtensions
{
    public static string GetTitle(this ColorLabel label) => label switch
    {
        ColorLabel.None => "None",
        ColorLabel.Red => "Red",
        ColorLabel.Yellow => "Yellow",
        ColorLabel.Green => "Green",
        ColorLabel.Blue => "Blue",
        _ => "None"
    };

    public static string GetHexColor(this ColorLabel label) => label switch
    {
        ColorLabel.Red => "#E53935",
        ColorLabel.Yellow => "#FDD835",
        ColorLabel.Green => "#43A047",
        ColorLabel.Blue => "#1E88E5",
        _ => "Transparent"
    };
}

public record struct CullingMetadata(
    int Rating = 0,
    PickStatus Pick = PickStatus.Unflagged,
    ColorLabel Color = ColorLabel.None,
    int? Orientation = null
)
{
    public int Rating { get; init; } = Math.Clamp(Rating, 0, 5);
}

public enum CullingFilterType
{
    All,
    PicksOnly,
    HideRejects,
    RejectsOnly,
    UnratedOnly,
    MinRating
}

public record CullingFilter(CullingFilterType Type, int MinStars = 0)
{
    public static readonly CullingFilter All = new(CullingFilterType.All);
    public static readonly CullingFilter PicksOnly = new(CullingFilterType.PicksOnly);
    public static readonly CullingFilter HideRejects = new(CullingFilterType.HideRejects);
    public static readonly CullingFilter RejectsOnly = new(CullingFilterType.RejectsOnly);
    public static readonly CullingFilter UnratedOnly = new(CullingFilterType.UnratedOnly);

    public string Title => Type switch
    {
        CullingFilterType.All => "All Files",
        CullingFilterType.PicksOnly => "Picks Only (P)",
        CullingFilterType.HideRejects => "Hide Rejects",
        CullingFilterType.RejectsOnly => "Rejects Only (X)",
        CullingFilterType.UnratedOnly => "Unrated Only",
        CullingFilterType.MinRating => $"{MinStars}+ Stars",
        _ => "All Files"
    };

    public bool Matches(CullingMetadata meta) => Type switch
    {
        CullingFilterType.All => true,
        CullingFilterType.PicksOnly => meta.Pick == PickStatus.Pick,
        CullingFilterType.HideRejects => meta.Pick != PickStatus.Reject,
        CullingFilterType.RejectsOnly => meta.Pick == PickStatus.Reject,
        CullingFilterType.UnratedOnly => meta.Rating == 0 && meta.Pick == PickStatus.Unflagged,
        CullingFilterType.MinRating => meta.Rating >= MinStars,
        _ => true
    };
}
