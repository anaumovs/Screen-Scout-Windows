using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ScoutCore;

public readonly record struct PointD(double X, double Y)
{
    [JsonIgnore] public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    public static PointD Mix(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
public readonly record struct SizeD(double Width, double Height);
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    [JsonIgnore] public SizeD Size => new(Width, Height);
    [JsonIgnore] public bool IsFinite => new[] { X, Y, Width, Height, Right, Bottom }.All(double.IsFinite);
    public bool Contains(PointD p) => p.IsFinite && p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool ContainsPixel(PointD p) => Contains(p) && p.X < Right && p.Y < Bottom;
    public bool Contains(RectD r) => Contains(new PointD(r.X, r.Y)) && Contains(new PointD(r.Right, r.Bottom));
    public RectD Inset(double d) => new(X + d, Y + d, Width - 2 * d, Height - 2 * d);
    public RectD Offset(double x, double y) => this with { X = X + x, Y = Y + y };
}
public sealed record MovementDisplay(string Identity, RectD Bounds);
public sealed record MovementBox(RectD Rect, bool Blocked)
{
    public bool Secondary { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
}
public sealed record RegionProfile(string DisplayIdentity, SizeD ReferenceSize, List<MovementBox> Boxes)
{
    public RectD? ReferenceBounds { get; init; }
    public MovementDomain? DomainFor(MovementDisplay display)
    {
        if (display.Identity != DisplayIdentity || display.Bounds.Size != ReferenceSize || Boxes.Count(b => !b.Secondary) > 32 || Boxes.Count(b => b.Secondary) > 1 ||
            Boxes.Any(b => !b.Rect.IsFinite || b.Rect.Width <= 0 || b.Rect.Height <= 0 || !new RectD(0, 0, ReferenceSize.Width, ReferenceSize.Height).Contains(b.Rect))) return null;
        return new(display.Bounds,
            Boxes.Where(b => !b.Blocked && !b.Secondary).Select(b => b.Rect.Offset(display.Bounds.X, display.Bounds.Y)).ToArray(),
            Boxes.Where(b => b.Blocked && !b.Secondary).Select(b => b.Rect.Offset(display.Bounds.X, display.Bounds.Y)).ToArray());
    }
}

public sealed class MovementDomain(RectD bounds, IReadOnlyList<RectD>? allowed = null, IReadOnlyList<RectD>? blocked = null)
{
    public RectD Bounds { get; } = bounds;
    public IReadOnlyList<RectD> Allowed { get; } = allowed ?? [];
    public IReadOnlyList<RectD> Blocked { get; } = blocked ?? [];
    public bool Contains(PointD p) => p.IsFinite && Bounds.Contains(p) && (Allowed.Count == 0 || Allowed.Any(r => r.Contains(p))) && !Blocked.Any(r => r.Inset(-1e-9).Contains(p));

    // Partition at every rectangle boundary; even tangent blocked-box contact is forbidden.
    public bool PermitsSegment(PointD a, PointD b)
    {
        if (!Contains(a) || !Contains(b)) return false;
        foreach (var rect in Blocked)
        {
            var r = rect.Inset(-1e-9);
            double lo = 0, hi = 1; bool possible = true;
            foreach (var (origin, delta, min, max) in new[] { (a.X, b.X - a.X, r.X, r.Right), (a.Y, b.Y - a.Y, r.Y, r.Bottom) })
            {
                if (delta == 0) { if (origin < min || origin > max) possible = false; }
                else { var t0 = (min - origin) / delta; var t1 = (max - origin) / delta; lo = Math.Max(lo, Math.Min(t0, t1)); hi = Math.Min(hi, Math.Max(t0, t1)); }
            }
            if (possible && lo <= hi) return false;
        }
        List<double> cuts = [0, 1];
        foreach (var r in new[] { Bounds }.Concat(Allowed).Concat(Blocked))
        {
            if (a.X != b.X) foreach (var x in new[] { r.X, r.Right }) { var t = (x - a.X) / (b.X - a.X); if (t > 0 && t < 1) cuts.Add(t); }
            if (a.Y != b.Y) foreach (var y in new[] { r.Y, r.Bottom }) { var t = (y - a.Y) / (b.Y - a.Y); if (t > 0 && t < 1) cuts.Add(t); }
        }
        cuts.Sort();
        if (cuts.Any(t => !Contains(PointD.Mix(a, b, t)))) return false;
        for (int i = 1; i < cuts.Count; i++) if (!Contains(PointD.Mix(a, b, (cuts[i - 1] + cuts[i]) / 2))) return false;
        return true;
    }
    private bool Covers(RectD rect)
    {
        if (!Bounds.Contains(rect) || Blocked.Any(r => r.X <= rect.Right && r.Right >= rect.X && r.Y <= rect.Bottom && r.Bottom >= rect.Y)) return false;
        if (Allowed.Count == 0) return true;
        var xs = new[] { rect.X, rect.Right }.Concat(Allowed.SelectMany(r => new[] { r.X, r.Right }).Where(x => x > rect.X && x < rect.Right)).Order().ToArray();
        var ys = new[] { rect.Y, rect.Bottom }.Concat(Allowed.SelectMany(r => new[] { r.Y, r.Bottom }).Where(y => y > rect.Y && y < rect.Bottom)).Order().ToArray();
        for (int x = 1; x < xs.Length; x++) for (int y = 1; y < ys.Length; y++)
            if (!Allowed.Any(r => r.Contains(new PointD((xs[x - 1] + xs[x]) / 2, (ys[y - 1] + ys[y]) / 2)))) return false;
        return true;
    }
    public bool PermitsCurve(MovementCurve curve)
    {
        var clock = Stopwatch.StartNew(); int budget = 8192;
        bool Certify(MovementCurve c, int depth)
        {
            if (--budget < 0 || clock.Elapsed.TotalSeconds > .015 || !Contains(c.A) || !Contains(c.D)) return false;
            var ps = new[] { c.A, c.B, c.C, c.D };
            var hull = new RectD(ps.Min(p => p.X), ps.Min(p => p.Y), ps.Max(p => p.X) - ps.Min(p => p.X), ps.Max(p => p.Y) - ps.Min(p => p.Y));
            if (Covers(hull)) return true;
            if (depth >= 18) return false;
            var (left, right) = c.Split();
            return Certify(left, depth + 1) && Certify(right, depth + 1);
        }
        return Certify(curve, 0);
    }
    public IReadOnlyList<PointD>? Route(PointD start, PointD end)
    {
        var clock = Stopwatch.StartNew();
        if (!Contains(start) || !Contains(end) || Allowed.Count + Blocked.Count > 32) return null;
        if (PermitsSegment(start, end)) return [start, end];
        var rects = new[] { Bounds }.Concat(Allowed).Concat(Blocked).ToArray();
        var xs = rects.SelectMany(r => new[] { r.X, r.Right }).Where(x => x >= Bounds.X && x <= Bounds.Right).Distinct().Order().ToArray();
        var ys = rects.SelectMany(r => new[] { r.Y, r.Bottom }).Where(y => y >= Bounds.Y && y <= Bounds.Bottom).Distinct().Order().ToArray();
        if (xs.Length < 2 || ys.Length < 2 || xs.Length * ys.Length > 5000) return null;
        int nx = xs.Length - 1, ny = ys.Length - 1;
        Dictionary<int, PointD> nodes = []; List<int> starts = []; HashSet<int> ends = [];
        for (int j = 0; j < ny; j++)
        {
            if (clock.Elapsed.TotalSeconds > .02) return null;
            for (int i = 0; i < nx; i++)
            {
                var id = j * nx + i; var p = new PointD((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2);
                if (!Contains(p)) continue;
                nodes[id] = p;
                var cell = new RectD(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]);
                if (cell.Contains(start) && PermitsSegment(start, p)) starts.Add(id);
                if (cell.Contains(end) && PermitsSegment(p, end)) ends.Add(id);
            }
        }
        List<int> queue = [.. starts]; var parents = starts.ToDictionary(x => x, _ => -1); int cursor = 0; int? found = null;
        while (cursor < queue.Count)
        {
            if (clock.Elapsed.TotalSeconds > .02) return null;
            var id = queue[cursor++];
            if (ends.Contains(id)) { found = id; break; }
            int i = id % nx, j = id / nx;
            foreach (var (x, y) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
            {
                if (x < 0 || x >= nx || y < 0 || y >= ny) continue;
                var next = y * nx + x;
                if (!parents.ContainsKey(next) && nodes.TryGetValue(next, out var p) && PermitsSegment(nodes[id], p)) { parents[next] = id; queue.Add(next); }
            }
        }
        if (found is null) return null;
        List<PointD> path = [end];
        for (int id = found.Value; id >= 0; id = parents[id]) path.Add(nodes[id]);
        path.Add(start); path.Reverse();
        List<PointD> simple = [start]; int index = 0;
        while (index < path.Count - 1)
        {
            int next = path.Count - 1;
            while (next > index + 1 && !PermitsSegment(path[index], path[next])) next--;
            simple.Add(path[next]); index = next;
        }
        return simple;
    }
}

public readonly record struct MovementCurve(PointD A, PointD B, PointD C, PointD D)
{
    public PointD Point(double t)
    {
        var ab = PointD.Mix(A, B, t); var bc = PointD.Mix(B, C, t); var cd = PointD.Mix(C, D, t);
        return PointD.Mix(PointD.Mix(ab, bc, t), PointD.Mix(bc, cd, t), t);
    }
    public (MovementCurve, MovementCurve) Split()
    {
        var ab = PointD.Mix(A, B, .5); var bc = PointD.Mix(B, C, .5); var cd = PointD.Mix(C, D, .5); var m = Point(.5);
        return (new(A, ab, PointD.Mix(ab, bc, .5), m), new(m, PointD.Mix(bc, cd, .5), cd, D));
    }
}
public sealed record MovementPath(IReadOnlyList<MovementCurve> Curves, IReadOnlyList<PointD> Points);
public static class CurvedMovement
{
    public static MovementPath? Plan(PointD start, PointD end, MovementDomain domain, Func<double, double, double> random)
    {
        var clock = Stopwatch.StartNew();
        var knots = domain.Route(start, end);
        if (knots is null || PointD.Distance(start, end) <= .0001) return null;
        var sign = random(0, 1) < .5 ? -1.0 : 1.0; var amount = random(.09, .22);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (clock.Elapsed.TotalSeconds > .04) return null;
            var scale = Math.Pow(.5, attempt); List<MovementCurve> curves = [];
            for (int i = 0; i < knots.Count - 1; i++)
            {
                if (clock.Elapsed.TotalSeconds > .04) return null;
                var a = knots[i]; var d = knots[i + 1]; var distance = PointD.Distance(a, d); PointD b, c;
                if (knots.Count == 2)
                {
                    var offset = distance * amount * scale * sign; var nx = -(d.Y - a.Y) / distance; var ny = (d.X - a.X) / distance;
                    var p = PointD.Mix(a, d, .30); var q = PointD.Mix(a, d, .70);
                    b = new(p.X + nx * offset, p.Y + ny * offset); c = new(q.X + nx * offset * .75, q.Y + ny * offset * .75);
                }
                else
                {
                    PointD Tangent(int k)
                    {
                        var before = knots[Math.Max(0, k - 1)]; var after = knots[Math.Min(knots.Count - 1, k + 1)]; var length = Math.Max(.0001, PointD.Distance(before, after));
                        return new((after.X - before.X) / length, (after.Y - before.Y) / length);
                    }
                    var t = Tangent(i); var u = Tangent(i + 1);
                    var left = Math.Min(distance, i > 0 ? PointD.Distance(knots[i - 1], a) : distance) * amount * scale;
                    var right = Math.Min(distance, i + 2 < knots.Count ? PointD.Distance(d, knots[i + 2]) : distance) * amount * scale;
                    b = new(a.X + t.X * left, a.Y + t.Y * left); c = new(d.X - u.X * right, d.Y - u.Y * right);
                }
                curves.Add(new(a, b, c, d));
            }
            if (!curves.All(c => clock.Elapsed.TotalSeconds <= .04 && domain.PermitsCurve(c))) continue;
            List<PointD> fine = [start];
            foreach (var c in curves) for (int i = 1; i <= 100; i++) fine.Add(c.Point(i / 100.0));
            List<double> lengths = [0];
            for (int i = 1; i < fine.Count; i++) lengths.Add(lengths[^1] + PointD.Distance(fine[i - 1], fine[i]));
            var total = lengths[^1]; int steps = Math.Clamp((int)Math.Ceiling(total / 6) + 12, 12, 160), index = 1;
            List<PointD> points = [];
            for (int i = 1; i <= steps; i++)
            {
                var t = (double)i / steps; var wanted = total * t * t * (3 - 2 * t);
                while (index < lengths.Count - 1 && lengths[index] < wanted) index++;
                var span = lengths[index] - lengths[index - 1];
                points.Add(i == steps ? end : PointD.Mix(fine[index - 1], fine[index], span > 0 ? (wanted - lengths[index - 1]) / span : 0));
            }
            var previous = start; bool valid = true;
            foreach (var p in points) { if (!domain.PermitsSegment(previous, p)) { valid = false; break; } previous = p; }
            if (valid) return new(curves, points);
        }
        return null;
    }
}
