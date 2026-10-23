namespace SoundBoardKing.Audio.Editing;

/// <summary>
/// Short-time Fourier transform of a stereo signal, framed as PyTorch's <c>stft</c>/<c>istft</c> with
/// <c>center=True</c> (reflect padding, periodic Hann window), which is what the voice model was trained
/// on. Left and right go through one complex FFT together: left as the real part, right as the imaginary.
/// </summary>
internal sealed class StereoStft
{
    private readonly int _nFft;
    private readonly int _hop;
    private readonly double[] _window;
    private readonly Fft _fft;
    private readonly double[] _re;
    private readonly double[] _im;

    public StereoStft(int nFft, int hop)
    {
        _nFft = nFft;
        _hop = hop;
        _window = new double[nFft];
        for (int i = 0; i < nFft; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / nFft);
        _fft = new Fft(nFft);
        _re = new double[nFft];
        _im = new double[nFft];
    }

    public int Bins => _nFft / 2 + 1;
    public int Hop => _hop;

    public int FrameCount(int length) => 1 + length / _hop;

    /// <summary>The spectrum of frame <paramref name="frame"/>, bins 0..<see cref="Bins"/>-1 of each channel.</summary>
    public void Analyze(float[] left, float[] right, int length, int frame,
        double[] leftRe, double[] leftIm, double[] rightRe, double[] rightIm)
    {
        int half = _nFft / 2;
        int origin = frame * _hop - half;
        for (int i = 0; i < _nFft; i++)
        {
            int j = Reflect(origin + i, length);
            _re[i] = left[j] * _window[i];
            _im[i] = right[j] * _window[i];
        }
        _fft.Transform(_re, _im, inverse: false);
        for (int k = 0; k < Bins; k++)
        {
            int mirror = k == 0 ? 0 : _nFft - k;
            double a = _re[k], b = _im[k], c = _re[mirror], d = _im[mirror];
            leftRe[k] = (a + c) / 2;
            leftIm[k] = (b - d) / 2;
            rightRe[k] = (b + d) / 2;
            rightIm[k] = (c - a) / 2;
        }
    }

    /// <summary>Collects frames by overlap-add and gives back the signal, as <c>istft</c> does.</summary>
    public Synthesis StartSynthesis(int frames) => new(this, frames);

    // Reflection without repeating the edge sample, like numpy's and torch's "reflect" padding.
    private static int Reflect(int i, int length)
    {
        if (length == 1)
            return 0;
        int period = 2 * (length - 1);
        i %= period;
        if (i < 0)
            i += period;
        return i < length ? i : period - i;
    }

    public sealed class Synthesis
    {
        private readonly StereoStft _stft;
        private readonly float[] _left;
        private readonly float[] _right;
        private readonly double[] _envelope;

        internal Synthesis(StereoStft stft, int frames)
        {
            _stft = stft;
            int length = stft._nFft + stft._hop * (frames - 1);
            _left = new float[length];
            _right = new float[length];
            _envelope = new double[length];
        }

        /// <summary>Adds frame <paramref name="frame"/> from its bins 0..Bins-1 (the rest of the spectrum mirrors them).</summary>
        public void Add(int frame, double[] leftRe, double[] leftIm, double[] rightRe, double[] rightIm)
        {
            var s = _stft;
            int n = s._nFft;
            int bins = s.Bins;
            // Z = L + iR over the whole circle, with L and R Hermitian so both come back real.
            for (int k = 0; k < bins; k++)
            {
                double lr = leftRe[k], li = leftIm[k], rr = rightRe[k], ri = rightIm[k];
                if (k == 0 || 2 * k == n)
                    li = ri = 0; // a real signal has no imaginary part there
                s._re[k] = lr - ri;
                s._im[k] = li + rr;
                if (k > 0 && 2 * k < n)
                {
                    // conj(L) + i conj(R) at n - k
                    s._re[n - k] = lr + ri;
                    s._im[n - k] = rr - li;
                }
            }
            s._fft.Transform(s._re, s._im, inverse: true);
            int origin = frame * s._hop;
            double scale = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                double w = s._window[i];
                _left[origin + i] += (float)(s._re[i] * scale * w);
                _right[origin + i] += (float)(s._im[i] * scale * w);
                _envelope[origin + i] += w * w;
            }
        }

        /// <summary>The signal, without the padding the analysis added: <paramref name="length"/> frames.</summary>
        public (float[] Left, float[] Right) Finish(int length)
        {
            int half = _stft._nFft / 2;
            var left = new float[length];
            var right = new float[length];
            for (int i = 0; i < length && half + i < _left.Length; i++)
            {
                double env = _envelope[half + i];
                if (env > 1e-11)
                {
                    left[i] = (float)(_left[half + i] / env);
                    right[i] = (float)(_right[half + i] / env);
                }
            }
            return (left, right);
        }
    }
}
