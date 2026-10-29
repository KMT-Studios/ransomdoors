namespace rans0m;

/// <summary>
/// Thread-safe <see cref="Random"/> wrapper. <see cref="System.Random"/> is explicitly not
/// thread safe, and this one is touched from the UI thread, the keyboard hook, and the
/// background coin generation threads all at once.
/// </summary>
public sealed class Rng
{
    private readonly Lock _gate = new();
    private readonly Random _random = new();

    public int Next(int maxValue) => Next(0, maxValue);

    public int Next(int minValue, int maxValue)
    {
        lock (_gate)
        {
            return _random.Next(minValue, maxValue);
        }
    }

    public double NextDouble()
    {
        lock (_gate)
        {
            return _random.NextDouble();
        }
    }

    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[Next(0, items.Count)];
    }
}