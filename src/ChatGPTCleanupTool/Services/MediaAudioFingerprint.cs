using System.Numerics;

namespace ChatGPTCleanupTool.Services;

internal static class MediaAudioFingerprint
{
    internal const int SampleRate = 11025;
    internal const double HopSeconds = .25;
    internal static (uint[] Hashes, double Energy) Create(double[] pcm, CancellationToken token)
    {
        const int size = 4096; int hop = (int)(SampleRate * HopSeconds);
        var hashes = new List<uint>(); var spectrum = new Complex[size];
        double energy = Math.Sqrt(pcm.Select(x => x * x).Average());
        for (int start = 0; start + size <= pcm.Length; start += hop)
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < size; i++) spectrum[i] = new(pcm[start + i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1))), 0);
            Fft(spectrum);
            var bands = new double[33];
            for (int band = 0; band < bands.Length; band++)
            {
                int from = Math.Max(1, (int)(100 * Math.Pow(50, band / 33.0) * size / SampleRate));
                int to = Math.Min(size / 2, (int)(100 * Math.Pow(50, (band + 1) / 33.0) * size / SampleRate));
                for (int k = from; k <= to; k++) bands[band] += spectrum[k].Magnitude * spectrum[k].Magnitude;
                bands[band] /= Math.Max(1, to - from + 1);
            }
            uint fingerprint = 0;
            for (int band = 0; band < 32; band++) if (bands[band] > bands[band + 1]) fingerprint |= 1U << band;
            hashes.Add(fingerprint);
        }
        return (hashes.ToArray(), energy);
    }
    internal static double Compare(uint[] a, uint[] b, CancellationToken token)
    {
        if (a.Length < 28 || b.Length < 28) return 0;
        double best = 0;
        // Allow up to ten seconds of leading silence/trim. Every member is compared with a fixed group anchor.
        for (int offset = -40; offset <= 40; offset++)
        {
            token.ThrowIfCancellationRequested(); int aStart = Math.Max(0, offset), bStart = Math.Max(0, -offset);
            int count = Math.Min(a.Length - aStart, b.Length - bStart);
            if (count < Math.Min(80, (int)(Math.Min(a.Length, b.Length) * .8))) continue;
            double mismatch = 0; int stable = 0;
            for (int i = 0; i < count; i++)
            {
                int difference = BitOperations.PopCount(a[aStart + i] ^ b[bStart + i]);
                mismatch += difference; if (difference <= 8) stable++;
            }
            double score = 1 - mismatch / (count * 32);
            if (stable >= count * .8 && score >= .82) best = Math.Max(best, score);
        }
        return best;
    }
    private static void Fft(Complex[] data)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                Complex factor = Complex.One;
                for (int k = 0; k < length / 2; k++)
                {
                    Complex even = data[start + k], odd = data[start + k + length / 2] * factor;
                    data[start + k] = even + odd; data[start + k + length / 2] = even - odd; factor *= step;
                }
            }
        }
    }
}
