namespace ScreenScout;

/// Filters device reports, including stationary absolute-position reports from tablets.
/// Keeps only the latest absolute sample per device; never a movement history.
internal sealed class DeviceMotionFilter(Func<uint, bool>? isOwnInput = null)
{
    private readonly Dictionary<nint, (int X, int Y)> absolute = [];
    public bool Observe(nint device, ushort flags, int x, int y, uint extraInfo = 0)
    {
        if (isOwnInput?.Invoke(extraInfo) == true) return false;
        // Zero is a valid handle for precision-touchpad reports, not an injection flag.
        if ((flags & 1) == 0)
        {
            absolute.Remove(device);
            return x != 0 || y != 0;
        }
        bool moved = !absolute.TryGetValue(device, out var previous) || previous != (x, y);
        if (!absolute.ContainsKey(device) && absolute.Count >= 32) absolute.Remove(absolute.Keys.First());
        absolute[device] = (x, y);
        return moved;
    }
    public void Remove(nint device) => absolute.Remove(device);
}
