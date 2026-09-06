namespace Sim;

/// Deterministic value noise. Every sample is a pure function of (seed, x, y),
/// so a world is reproducible from its seed alone and chunks can be generated in
/// any order, or regenerated later, and come out identical.
///
/// No global RNG state anywhere: that is what would make generation order
/// matter, and the whole sim depends on it not mattering.
public static class Noise
{
    /// Integer hash. Deterministic across runs and platforms, which a shared
    /// Random instance would not be once generation order changed.
    public static uint Hash(int seed, int x, int y)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)y * 0x85EBCA6Bu;
            h ^= h >> 15;
            h *= 0x2545F491u;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }
    }

    /// Uniform in [0, 1).
    public static double Unit(int seed, int x, int y) => Hash(seed, x, y) / 4294967296.0;

    /// Uniform in [0, bound).
    public static int Range(int seed, int x, int y, int bound) =>
        bound <= 0 ? 0 : (int)(Hash(seed, x, y) % (uint)bound);

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

    /// Bilinear value noise at a given cell size, in [0, 1].
    public static double Value(int seed, double x, double y, int cell)
    {
        if (cell < 1) cell = 1;

        var fx = x / cell;
        var fy = y / cell;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = Smooth(fx - x0);
        var ty = Smooth(fy - y0);

        var a = Unit(seed, x0, y0);
        var b = Unit(seed, x0 + 1, y0);
        var c = Unit(seed, x0, y0 + 1);
        var d = Unit(seed, x0 + 1, y0 + 1);

        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    /// Stacked octaves, each half the size and half the weight. Returns [0, 1].
    public static double Fractal(int seed, double x, double y, int cell, int octaves)
    {
        var total = 0.0;
        var amplitude = 1.0;
        var normalise = 0.0;

        for (var i = 0; i < octaves; i++)
        {
            total += Value(seed + i * 7919, x, y, Math.Max(1, cell >> i)) * amplitude;
            normalise += amplitude;
            amplitude *= 0.5;
        }

        return total / normalise;
    }
}
