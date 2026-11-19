namespace WinGlance.Model;

/// <summary>
/// Multi-image comparison layout mode (1-UP -> 2-UP -> 4-UP).
/// </summary>
public enum MultiUpMode
{
    Single = 1,
    TwoUp = 2,
    FourUp = 4
}

public static class MultiUpModeExtensions
{
    public static MultiUpMode Next(this MultiUpMode mode) => mode switch
    {
        MultiUpMode.Single => MultiUpMode.TwoUp,
        MultiUpMode.TwoUp => MultiUpMode.FourUp,
        _ => MultiUpMode.Single
    };
}
