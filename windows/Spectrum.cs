using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Stillwave
{
    // Listens to what the PC is playing (WASAPI loopback) and draws it.
    public class Spectrum
    {
        public static readonly string[] Styles = { "Bars", "LED", "Mirrored", "Wave" };
        public static readonly string[] Themes = { "Ocean", "Fire", "Orchid", "Matrix", "Ice", "Rainbow" };
        public static readonly int[] BandChoices = { 24, 40, 56, 80 };
        public static readonly string[] GainNames = { "Low", "Normal", "High", "Max" };
        static readonly double[] GainDb = { 60, 70, 80, 90 };
        public static readonly string[] SmoothNames = { "Snappy", "Balanced", "Smooth" };
        static readonly double[] Attack = { 0.85, 0.6, 0.35 };
        static readonly double[] Decay = { 0.78, 0.88, 0.94 };

        // user options, saved by the window as one string
        public int Style = 0, Theme = 0, BandsIdx = 2, Gain = 1, Smooth = 1;
        public bool Peaks = true, Visible = true;

        const int N = 2048;             // FFT size
        readonly Canvas canvas;
        Shape[] bars = new Shape[0];
        Shape[] caps = new Shape[0];
        Polygon fill; Polyline line;
        double[] level = new double[0], peak = new double[0];
        int bands;
        bool dirty = true;
        readonly float[] ring = new float[N];
        readonly double[] window = new double[N];
        readonly object gate = new object();
        int pos;
        WasapiLoopbackCapture cap;
        int rate = 48000;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };

        public Spectrum(Canvas c)
        {
            canvas = c;
            for (int i = 0; i < N; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            timer.Tick += (s, e) => Draw();
        }

        public string Serialize() => string.Join(",", Style, Theme, BandsIdx, Gain, Smooth, Peaks ? 1 : 0, Visible ? 1 : 0);

        public void Load(string s)
        {
            try
            {
                var p = s.Split(',');
                Style = Math.Clamp(int.Parse(p[0]), 0, Styles.Length - 1);
                Theme = Math.Clamp(int.Parse(p[1]), 0, Themes.Length - 1);
                BandsIdx = Math.Clamp(int.Parse(p[2]), 0, BandChoices.Length - 1);
                Gain = Math.Clamp(int.Parse(p[3]), 0, GainNames.Length - 1);
                Smooth = Math.Clamp(int.Parse(p[4]), 0, SmoothNames.Length - 1);
                Peaks = p[5] == "1";
                Visible = p[6] == "1";
            }
            catch { }
        }

        public void Changed() { dirty = true; }

        public void Start()
        {
            try
            {
                cap = new WasapiLoopbackCapture();
                rate = cap.WaveFormat.SampleRate;
                cap.DataAvailable += OnData;
                cap.StartRecording();
            }
            catch { cap = null; }
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            try { cap?.StopRecording(); cap?.Dispose(); } catch { }
            cap = null;
        }

        void OnData(object s, WaveInEventArgs e)
        {
            var wf = cap.WaveFormat;
            int ch = wf.Channels;
            bool isFloat = wf.BitsPerSample == 32;   // loopback is 32-bit float in practice
            int step = ch * (wf.BitsPerSample / 8);
            lock (gate)
            {
                for (int o = 0; o + step <= e.BytesRecorded; o += step)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int at = o + c * (wf.BitsPerSample / 8);
                        sum += isFloat ? BitConverter.ToSingle(e.Buffer, at) : BitConverter.ToInt16(e.Buffer, at) / 32768f;
                    }
                    ring[pos] = sum / ch;
                    pos = (pos + 1) % N;
                }
            }
        }

        // ---------- look ----------
        static (Color lo, Color hi) Pair(int theme)
        {
            switch (theme)
            {
                case 1: return (Color.FromRgb(0xb0, 0x2a, 0x1e), Color.FromRgb(0xff, 0xd2, 0x3f));
                case 2: return (Color.FromRgb(0x5b, 0x2a, 0x8c), Color.FromRgb(0xff, 0x7a, 0xd9));
                case 3: return (Color.FromRgb(0x08, 0x5a, 0x24), Color.FromRgb(0x7d, 0xff, 0x7d));
                case 4: return (Color.FromRgb(0x7a, 0x8d, 0xa3), Color.FromRgb(0xff, 0xff, 0xff));
                default: return (Color.FromRgb(0x2f, 0x6f, 0xb5), Color.FromRgb(0x7f, 0xe0, 0xd0));
            }
        }

        static Color Hue(double h)   // h in 0..1
        {
            double r = Math.Abs(h * 6 - 3) - 1, g = 2 - Math.Abs(h * 6 - 2), b = 2 - Math.Abs(h * 6 - 4);
            byte f(double x) => (byte)(Math.Clamp(x, 0, 1) * 215 + 40);
            return Color.FromRgb(f(r), f(g), f(b));
        }

        Brush BarBrush(int i)
        {
            if (Theme == 5) return new SolidColorBrush(Hue((double)i / Math.Max(1, bands - 1) * 0.85));
            var (lo, hi) = Pair(Theme);
            return new LinearGradientBrush(lo, hi, 90);
        }

        void Rebuild()
        {
            dirty = false;
            canvas.Children.Clear();
            bands = BandChoices[BandsIdx];
            level = new double[bands];
            peak = new double[bands];
            bars = new Shape[bands];
            caps = new Shape[bands];
            fill = null; line = null;
            canvas.Visibility = Visibility.Visible;

            if (Style == 3)
            {
                var (lo, hi) = Pair(Theme == 5 ? 0 : Theme);
                Brush top = Theme == 5 ? new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Hue(0), 0), new GradientStop(Hue(0.3), 0.35), new GradientStop(Hue(0.6), 0.7), new GradientStop(Hue(0.85), 1) }, 0) : new SolidColorBrush(hi);
                var fb = new LinearGradientBrush(Color.FromArgb(0x10, lo.R, lo.G, lo.B), Color.FromArgb(0x90, hi.R, hi.G, hi.B), 90);
                fill = new Polygon { Fill = fb };
                line = new Polyline { Stroke = top, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                canvas.Children.Add(fill);
                canvas.Children.Add(line);
                return;
            }

            for (int i = 0; i < bands; i++)
            {
                bars[i] = new Rectangle { Fill = BarBrush(i), RadiusX = Style == 1 ? 0 : 1.5, RadiusY = Style == 1 ? 0 : 1.5 };
                if (Style == 1)   // LED look: carve the bar into little blocks
                {
                    var tile = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 4, 3))))
                    { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
                    bars[i].OpacityMask = tile;
                }
                caps[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0xcf, 0xe0, 0xf2)), Height = 2 };
                canvas.Children.Add(bars[i]);
                canvas.Children.Add(caps[i]);
            }
        }

        void Draw()
        {
            if (!Visible) { if (canvas.Visibility == Visibility.Visible) canvas.Visibility = Visibility.Collapsed; return; }
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;
            if (dirty || bars.Length != BandChoices[BandsIdx] || canvas.Visibility != Visibility.Visible) Rebuild();

            var re = new double[N];
            var im = new double[N];
            lock (gate)
                for (int i = 0; i < N; i++) re[i] = ring[(pos + i) % N] * window[i];
            Fft(re, im);

            // log-spaced bands from 40 Hz to 16 kHz
            double lo = 40, hi = Math.Min(16000, rate / 2.0);
            double binHz = (double)rate / N;
            double slot = w / bands;
            double span = 60, floor = GainDb[Gain], atk = Attack[Smooth], dec = Decay[Smooth];
            for (int b = 0; b < bands; b++)
            {
                double f0 = lo * Math.Pow(hi / lo, (double)b / bands);
                double f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / bands);
                int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
                double m = 0;
                for (int i = i0; i < i1 && i < N / 2; i++) m = Math.Max(m, Math.Sqrt(re[i] * re[i] + im[i] * im[i]));
                double db = 20 * Math.Log10(m / (N / 4.0) + 1e-9);          // 0 dB = full scale
                double v = Math.Clamp((db + floor) / span, 0, 1);
                level[b] = v > level[b] ? level[b] + (v - level[b]) * atk : level[b] * dec;
                peak[b] = level[b] > peak[b] ? level[b] : Math.Max(0, peak[b] - 0.012);
            }

            if (Style == 3)
            {
                var pts = new PointCollection();
                for (int b = 0; b < bands; b++) pts.Add(new Point(b * slot + slot / 2, h - level[b] * (h - 2) - 1));
                var area = new PointCollection(pts);
                area.Insert(0, new Point(0, h)); area.Add(new Point(w, h));
                line.Points = pts;
                fill.Points = area;
                return;
            }

            for (int b = 0; b < bands; b++)
            {
                double bh = Math.Max(0, level[b] * h);
                double gap = bands > 60 ? 1 : 2;
                bars[b].Width = Math.Max(1, slot - gap);
                caps[b].Width = bars[b].Width;
                Canvas.SetLeft(bars[b], b * slot + gap / 2);
                Canvas.SetLeft(caps[b], b * slot + gap / 2);
                if (Style == 2)   // mirrored around the middle
                {
                    bars[b].Height = bh;
                    Canvas.SetTop(bars[b], (h - bh) / 2);
                    caps[b].Visibility = Visibility.Collapsed;
                }
                else
                {
                    bars[b].Height = bh;
                    Canvas.SetTop(bars[b], h - bh);
                    caps[b].Visibility = Peaks ? Visibility.Visible : Visibility.Collapsed;
                    Canvas.SetTop(caps[b], Math.Max(0, h - peak[b] * h - 3));
                }
            }
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }
    }
}
