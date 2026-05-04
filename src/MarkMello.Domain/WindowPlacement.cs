namespace MarkMello.Domain;

/// <summary>
/// Последнее пользовательское положение главного окна.
/// Размер хранится в Avalonia DIPs, позиция — в экранных пикселях.
/// </summary>
public sealed record WindowPlacement(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsMaximized)
{
    public static WindowPlacement? Normalize(WindowPlacement? placement)
    {
        if (placement is null)
        {
            return null;
        }

        if (!IsFinite(placement.X)
            || !IsFinite(placement.Y)
            || !IsFinite(placement.Width)
            || !IsFinite(placement.Height)
            || placement.Width <= 0
            || placement.Height <= 0)
        {
            return null;
        }

        return placement;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
