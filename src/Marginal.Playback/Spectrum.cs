// COPIED from AlbumWall (src/AlbumWall.Playback/Spectrum.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall - the meters' readings, and the 31-band analyzer's arithmetic.
//
// THIRD-OCTAVE BANDS, 20 Hz to 20 kHz: the ISO 266 centres (1 kHz x 10^(n/10)),
// each band a tenth of a decade wide, as a hardware RTA draws them (2026-10-04,
// "can we add a 31 spectrum display option"). Read from one 4096-point
// transform of the samples sounding now (HymieEngine.Bands) rather than a bank
// of 31 filters: 30 times a second that is a few hundred microseconds on the
// app's thread, and nothing at all on the audio thread, which only keeps the
// samples. The price is the bottom of the scale: at 44.1 kHz a bin is 10.8 Hz
// and the 20 Hz band is 4.6 Hz wide, so the lowest bands share bins and read
// smoothed. Each band takes the bins it overlaps, in proportion to the overlap.

namespace Marginal.Playback;

/// RMS and peak of the block sounding now, in dBFS; negative infinity is silence.
public readonly record struct MeterLevels(double RmsLeft, double RmsRight, double PeakLeft, double PeakRight)
{
    public static readonly MeterLevels Silent = new(double.NegativeInfinity, double.NegativeInfinity,
                                                    double.NegativeInfinity, double.NegativeInfinity);
}

public static class Spectrum
{
    public const int Size = 4096;
    public const int BandCount = 31;

    /// The bands' centres in Hz, for labelling.
    public static readonly double[] Centres =
        Enumerable.Range(-17, BandCount).Select(n => 1000 * Math.Pow(10, n / 10.0)).ToArray();

    private static readonly float[] Hann = Enumerable.Range(0, Size)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size))).ToArray();

    private static readonly double[] Cos = Enumerable.Range(0, Size / 2).Select(i => Math.Cos(-2 * Math.PI * i / Size)).ToArray();
    private static readonly double[] Sin = Enumerable.Range(0, Size / 2).Select(i => Math.Sin(-2 * Math.PI * i / Size)).ToArray();

    // A sine of amplitude A puts A^2 * 3N^2/32 into its half of a Hann-windowed
    // transform (Parseval, with the window's sum of squares 3N/8): dividing by
    // that makes a full-scale sine read 0 dB in its band.
    private static readonly double FullScale = 3.0 * Size * Size / 32;

    // Only ever used from the app's thread (the meter timer).
    private static readonly double[] Re = new double[Size];
    private static readonly double[] Im = new double[Size];
    private static readonly double[] Power = new double[Size / 2 + 1];

    /// The bands of `samples` (Size of them, mono) at `rate`, scaled by `gain`, in dB.
    public static void ThirdOctaves(ReadOnlySpan<float> samples, int rate, float gain, Span<float> bands)
    {
        for (var i = 0; i < Size; i++) { Re[i] = samples[i] * Hann[i]; Im[i] = 0; }
        Fft(Re, Im);
        var norm = (double)gain * gain / FullScale;
        for (var k = 0; k <= Size / 2; k++) Power[k] = (Re[k] * Re[k] + Im[k] * Im[k]) * norm;

        var bin = (double)rate / Size;
        var edge = Math.Pow(10, 1 / 20.0);
        for (var b = 0; b < BandCount; b++)
        {
            var lo = Centres[b] / edge / bin;           // in bins
            var hi = Math.Min(Centres[b] * edge / bin, Size / 2 + 0.5);
            double p = 0;
            // Bin k covers [k - 0.5, k + 0.5).
            for (var k = Math.Max(0, (int)Math.Floor(lo + 0.5)); k <= Size / 2 && k - 0.5 < hi; k++)
            {
                var overlap = Math.Min(hi, k + 0.5) - Math.Max(lo, k - 0.5);
                if (overlap > 0) p += Power[k] * overlap;
            }
            bands[b] = p > 0 ? (float)(10 * Math.Log10(p)) : float.NegativeInfinity;
        }
    }

    /// In-place radix-2 transform of Size points.
    private static void Fft(double[] re, double[] im)
    {
        for (int i = 1, j = 0; i < Size; i++)
        {
            var bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= Size; len <<= 1)
        {
            var step = Size / len;
            var half = len >> 1;
            for (var i = 0; i < Size; i += len)
                for (var k = 0; k < half; k++)
                {
                    double wr = Cos[k * step], wi = Sin[k * step];
                    int a = i + k, b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}
