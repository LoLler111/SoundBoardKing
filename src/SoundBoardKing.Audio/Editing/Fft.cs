namespace SoundBoardKing.Audio.Editing;

/// <summary>
/// Complex FFT of any length: radix-2 for powers of two, Bluestein's chirp-z for the rest (the voice
/// model works on 7680-point frames). Not thread safe: one instance per thread.
/// </summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly Radix2? _direct;

    // Bluestein: x is multiplied by the chirp, convolved with its conjugate through a power-of-two FFT.
    private readonly Radix2? _inner;
    private readonly double[]? _chirpRe;
    private readonly double[]? _chirpIm;
    private readonly double[]? _kernelRe;
    private readonly double[]? _kernelIm;
    private readonly double[]? _workRe;
    private readonly double[]? _workIm;

    public Fft(int n)
    {
        _n = n;
        if ((n & (n - 1)) == 0)
        {
            _direct = new Radix2(n);
            return;
        }

        int m = 1;
        while (m < 2 * n - 1)
            m <<= 1;
        _inner = new Radix2(m);
        _chirpRe = new double[n];
        _chirpIm = new double[n];
        for (long k = 0; k < n; k++)
        {
            // k² mod 2n keeps the angle exact for long frames.
            double angle = Math.PI * (k * k % (2L * n)) / n;
            _chirpRe[k] = Math.Cos(angle);
            _chirpIm[k] = -Math.Sin(angle);
        }
        _kernelRe = new double[m];
        _kernelIm = new double[m];
        _kernelRe[0] = _chirpRe[0];
        _kernelIm[0] = -_chirpIm[0];
        for (int k = 1; k < n; k++)
        {
            _kernelRe[k] = _kernelRe[m - k] = _chirpRe[k];
            _kernelIm[k] = _kernelIm[m - k] = -_chirpIm[k];
        }
        _inner.Transform(_kernelRe, _kernelIm, inverse: false);
        _workRe = new double[m];
        _workIm = new double[m];
    }

    public int Length => _n;

    /// <summary>In place. The inverse is not scaled: divide by the length to undo a forward transform.</summary>
    public void Transform(double[] re, double[] im, bool inverse)
    {
        if (_direct is not null)
        {
            _direct.Transform(re, im, inverse);
            return;
        }

        // The inverse is the forward transform of the conjugate, conjugated.
        if (inverse)
        {
            for (int k = 0; k < _n; k++)
                im[k] = -im[k];
        }
        var wr = _workRe!;
        var wi = _workIm!;
        Array.Clear(wr);
        Array.Clear(wi);
        for (int k = 0; k < _n; k++)
        {
            wr[k] = re[k] * _chirpRe![k] - im[k] * _chirpIm![k];
            wi[k] = re[k] * _chirpIm[k] + im[k] * _chirpRe[k];
        }
        _inner!.Transform(wr, wi, inverse: false);
        for (int k = 0; k < wr.Length; k++)
        {
            double r = wr[k] * _kernelRe![k] - wi[k] * _kernelIm![k];
            wi[k] = wr[k] * _kernelIm[k] + wi[k] * _kernelRe[k];
            wr[k] = r;
        }
        _inner.Transform(wr, wi, inverse: true);
        double scale = 1.0 / wr.Length;
        for (int k = 0; k < _n; k++)
        {
            double r = wr[k] * scale, i = wi[k] * scale;
            re[k] = r * _chirpRe![k] - i * _chirpIm![k];
            im[k] = r * _chirpIm[k] + i * _chirpRe[k];
            if (inverse)
                im[k] = -im[k];
        }
    }

    private sealed class Radix2
    {
        private readonly int _n;
        private readonly int[] _reverse;
        private readonly double[] _cos;
        private readonly double[] _sin;

        public Radix2(int n)
        {
            _n = n;
            _reverse = new int[n];
            int bits = 0;
            while (1 << bits < n)
                bits++;
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                _reverse[i] = r;
            }
            _cos = new double[n / 2];
            _sin = new double[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                _cos[i] = Math.Cos(2 * Math.PI * i / n);
                _sin[i] = -Math.Sin(2 * Math.PI * i / n);
            }
        }

        public void Transform(double[] re, double[] im, bool inverse)
        {
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                int j = _reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            double sign = inverse ? -1 : 1;
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                int step = n / size;
                for (int start = 0; start < n; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wr = _cos[k * step], wi = sign * _sin[k * step];
                        int a = start + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
