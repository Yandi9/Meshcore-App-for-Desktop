namespace MC1.Windows.Controls;

/// <summary>
/// The start-up animation's sound, synthesised (no audio files) and timed to <see cref="StartupSplash"/>'s timeline:
/// data blips as the letters build up into a rising arpeggio, a soft power-up when the frame appears, an electric zap
/// for every radio-wave ray that hits the frame over a charge hum rising in pitch, and, once it's fully charged, a deep
/// shock-wave boom with two smaller ones for the ripple. Stereo: letters left to right, rays from the side they come in.
/// </summary>
public static class StartupSound
{
    private const int Rate = 44100;
    private static readonly Lazy<byte[]> s_wav = new(Render, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The whole sound as a 16-bit stereo WAV file (made once).</summary>
    public static byte[] Wav => s_wav.Value;

    /// <summary>Length in seconds (longer than the animation: the shock wave rumbles out).</summary>
    public static double Seconds => StartupSplash.TotalDuration / 1000 + 0.7;

    private static byte[] Render()
    {
        var n = (int)(Seconds * Rate);
        var left = new float[n];
        var right = new float[n];
        var rng = new Random(7);

        // 1. The letters build up: falling data blocks tick, and each letter lands on a note (A major pentatonic, rising).
        double[] notes = [440, 493.88, 554.37, 659.25, 739.99, 880, 987.77, 1108.73];
        for (var i = 0; i < 8; i++)
        {
            var start = (StartupSplash.LetterStart + i * StartupSplash.LetterStagger) / 1000;
            var pan = -0.55 + 1.1 * i / 7;
            for (var k = 0; k < 5; k++)
            {
                var t = start + rng.NextDouble() * (StartupSplash.LetterResolve - 40) / 1000;
                Tick(left, right, t, 2200 + rng.NextDouble() * 2200, 0.045, pan + (rng.NextDouble() - 0.5) * 0.3);
            }
            Blip(left, right, start + StartupSplash.LetterResolve / 1000 - 0.02, notes[i], 0.2, pan);
        }

        // 2. The word moves up and the frame powers on: a low thump and a rising whoosh.
        var frame = StartupSplash.FrameStart / 1000;
        Thump(left, right, frame, 95, 45, 0.30, 0.13);
        Whoosh(left, right, StartupSplash.TextMoveStart / 1000, (StartupSplash.FirstHit - StartupSplash.TextMoveStart) / 1000, 350, 2600, 0.11, rng);

        // 3. Rays hit the frame (a zap each, from the side the ray comes in) while the charge hum rises to the top note.
        var hit0 = StartupSplash.FirstHit / 1000;
        var charged = StartupSplash.FullyCharged / 1000;
        Charge(left, right, hit0 - 0.05, charged, 110, 440, 0.15);
        for (var r = 0; r < StartupSplash.RayCount; r++)
        {
            var pan = Math.Cos(StartupSplash.RayAngles[r] * Math.PI / 180) * 0.8;
            for (var f = 0; f < StartupSplash.FrontsPerRay; f++)
            {
                var t = (StartupSplash.FirstHit + r * StartupSplash.HitStagger + f * StartupSplash.FrontSpacing) / 1000;
                double[] level = [0.13, 0.085, 0.055];
                Zap(left, right, t, 3000 * (1 + 0.05 * r), level[f], pan, rng);
            }
        }

        // 4. Fully charged: the shock wave. A short rush inwards, then a deep boom (sub-bass falling in pitch, with a
        //    crack on impact, a rumbling pressure tail and a low gong-like body); the ripple's two echoes are smaller
        //    booms, left then right.
        var hit = StartupSplash.RippleStart / 1000;
        Swell(left, right, hit - 0.16, 0.16, 0.16, rng);
        Boom(left, right, hit, 1.0, 120, 34, 0.3, 0, rng, rumble: 0.8, rumbleDecay: 0.35);
        Bell(left, right, hit, 220, 0.16, 0, 0.6);
        // The aftershocks: lighter thuds (higher, short, little rumble) so they don't drag the low end out.
        for (var k = 1; k < StartupSplash.RippleCount; k++)
            Boom(left, right, hit + k * StartupSplash.RippleStagger / 1000, k == 1 ? 0.34 : 0.2, 260, 120, 0.16, k == 1 ? -0.55 : 0.55, rng,
                rumble: 0.35, rumbleDecay: 0.18, rumbleFrom: 420);

        return Encode(Master(left, right));
    }

    // MARK: Sounds

    /// <summary>A tiny 8-bit tick: a square wave burst of a few milliseconds.</summary>
    private static void Tick(float[] l, float[] r, double at, double freq, double amp, double pan)
    {
        var len = (int)(0.012 * Rate);
        var i0 = (int)(at * Rate);
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var v = Math.Sign(Math.Sin(2 * Math.PI * freq * t)) * Math.Exp(-t / 0.0025) * amp;
            Add(l, r, i0 + j, v, pan);
        }
    }

    /// <summary>A soft retro note: a sine with a little square (odd harmonics) on top, quick to decay.</summary>
    private static void Blip(float[] l, float[] r, double at, double freq, double amp, double pan)
    {
        var len = (int)(0.22 * Rate);
        var i0 = (int)(at * Rate);
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var w = 2 * Math.PI * freq * t;
            var square = Math.Sin(w) + Math.Sin(3 * w) / 3 + Math.Sin(5 * w) / 5 + Math.Sin(7 * w) / 7;
            var tone = 0.55 * Math.Sin(w) + 0.45 * square * 0.8;
            var env = Math.Min(1, t / 0.003) * Math.Exp(-t / 0.055);
            Add(l, r, i0 + j, tone * env * amp, pan);
        }
    }

    /// <summary>A low "power on" thump: a sine sliding down in pitch.</summary>
    private static void Thump(float[] l, float[] r, double at, double from, double to, double amp, double decay)
    {
        var len = (int)(decay * 5 * Rate);
        var i0 = (int)(at * Rate);
        double phase = 0;
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var f = to + (from - to) * Math.Exp(-t / 0.06);
            phase += 2 * Math.PI * f / Rate;
            var env = Math.Min(1, t / 0.006) * Math.Exp(-t / decay);
            Add(l, r, i0 + j, Math.Sin(phase) * env * amp, 0);
        }
    }

    /// <summary>Noise through a band-pass filter sweeping up: an airy whoosh.</summary>
    private static void Whoosh(float[] l, float[] r, double at, double dur, double fromHz, double toHz, double amp, Random rng)
    {
        var len = (int)(dur * Rate);
        var i0 = (int)(at * Rate);
        var bl = new Biquad();
        var br = new Biquad();
        for (var j = 0; j < len; j++)
        {
            var p = (double)j / len;
            if (j % 32 == 0)
            {
                var fc = fromHz * Math.Pow(toHz / fromHz, p);
                bl.BandPass(fc, 2.2);
                br.BandPass(fc * 1.04, 2.2);
            }
            var env = Math.Pow(Math.Sin(Math.PI * Math.Min(1, p * 1.15)), 2) * amp;
            var nl = bl.Run(rng.NextDouble() * 2 - 1);
            var nr = br.Run(rng.NextDouble() * 2 - 1);
            if (i0 + j < l.Length)
            {
                l[i0 + j] += (float)(nl * env * 3);
                r[i0 + j] += (float)(nr * env * 3);
            }
        }
    }

    /// <summary>The charge hum: a soft saw rising from <paramref name="from"/> to <paramref name="to"/> Hz and growing louder.</summary>
    private static void Charge(float[] l, float[] r, double start, double end, double from, double to, double amp)
    {
        var i0 = (int)(start * Rate);
        var len = (int)((end - start + 0.05) * Rate);
        double phaseL = 0, phaseR = 0;
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var p = Math.Min(1, t / (end - start));
            var f = from * Math.Pow(to / from, Math.Pow(p, 1.25)) * (1 + 0.005 * Math.Sin(2 * Math.PI * 6 * t));
            phaseL += 2 * Math.PI * f * 0.997 / Rate;
            phaseR += 2 * Math.PI * f * 1.003 / Rate;
            var env = (0.12 + 0.88 * Math.Pow(p, 1.6)) * Math.Min(1, t / 0.04) * (t > end - start ? Math.Exp(-(t - (end - start)) / 0.012) : 1);
            Add(l, r, i0 + j, Saw(phaseL) * env * amp, -1);
            Add(l, r, i0 + j, Saw(phaseR) * env * amp, 1);
        }
    }

    private static double Saw(double phase)
    {
        double v = 0;
        for (var h = 1; h <= 6; h++) v += Math.Sin(h * phase) / Math.Pow(h, 1.4);
        return v * 0.6;
    }

    /// <summary>A ray hitting the frame: a quick downward chirp with a click, panned to the ray's side.</summary>
    private static void Zap(float[] l, float[] r, double at, double freq, double amp, double pan, Random rng)
    {
        var len = (int)(0.07 * Rate);
        var i0 = (int)(at * Rate);
        double phase = 0;
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var f = freq * (0.45 + 0.55 * Math.Exp(-t / 0.012));
            phase += 2 * Math.PI * f / Rate;
            var env = Math.Min(1, t / 0.0008) * Math.Exp(-t / 0.016);
            var click = t < 0.003 ? (rng.NextDouble() * 2 - 1) * 0.5 * (1 - t / 0.003) : 0;
            Add(l, r, i0 + j, (Math.Sin(phase) + click) * env * amp, pan);
        }
    }

    /// <summary>
    /// A deep boom: a sine diving from <paramref name="fromHz"/> to <paramref name="toHz"/> (partly overdriven, so its
    /// harmonics carry the weight on small speakers too), a crack of noise on impact, and low rumbling noise that rolls
    /// off (wide in stereo, leaning to <paramref name="pan"/>).
    /// </summary>
    private static void Boom(float[] l, float[] r, double at, double amp, double fromHz, double toHz, double decay, double pan, Random rng,
        double rumble = 1, double rumbleDecay = 0.9, double rumbleFrom = 160)
    {
        var len = (int)(Math.Max(decay, rumbleDecay) * 6 * Rate);
        var i0 = (int)(at * Rate);
        double phase = 0, punchPhase = 0;
        var lpL = new Biquad();
        var lpR = new Biquad();
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            var f = toHz + (fromHz - toHz) * Math.Exp(-t / 0.09);
            phase += 2 * Math.PI * f / Rate;
            var env = Math.Min(1, t / 0.003) * Math.Exp(-t / decay);
            var sine = Math.Sin(phase);
            var driven = Math.Tanh(5 * sine) / Math.Tanh(5);
            var body = (0.5 * sine + 0.5 * driven) * env * amp * 0.8;
            // The punch: the same dive two octaves up, overdriven and short — what small speakers can actually play.
            var pf = 4 * toHz + (4 * fromHz - 4 * toHz) * Math.Exp(-t / 0.05);
            punchPhase += 2 * Math.PI * pf / Rate;
            var punch = Math.Tanh(4 * Math.Sin(punchPhase)) / Math.Tanh(4) * Math.Min(1, t / 0.002) * Math.Exp(-t / 0.14) * amp * 0.6;
            Add(l, r, i0 + j, body + punch, 0);

            if (j % 32 == 0)
            {
                var fc = rumbleFrom + 700 * Math.Exp(-t / 0.12);
                lpL.LowPass(fc, 0.9);
                lpR.LowPass(fc * 1.06, 0.9);
            }
            var rumbleEnv = Math.Min(1, t / 0.008) * Math.Exp(-t / rumbleDecay) * amp * 1.6 * rumble;
            var nl = lpL.Run(rng.NextDouble() * 2 - 1) * rumbleEnv;
            var nr = lpR.Run(rng.NextDouble() * 2 - 1) * rumbleEnv;
            var crack = t < 0.025 ? (rng.NextDouble() * 2 - 1) * Math.Exp(-t / 0.004) * amp * 0.35 : 0;
            var k = i0 + j;
            if (k >= l.Length) continue;
            var lean = (pan + 1) / 2;
            l[k] += (float)((nl + crack) * (1.2 - 0.4 * lean));
            r[k] += (float)((nr + crack) * (0.8 + 0.4 * lean));
        }
    }

    /// <summary>The rush into the shock wave: low noise swelling up and cut off at the hit.</summary>
    private static void Swell(float[] l, float[] r, double at, double dur, double amp, Random rng)
    {
        var len = (int)(dur * Rate);
        var i0 = (int)(at * Rate);
        var bl = new Biquad();
        var br = new Biquad();
        for (var j = 0; j < len; j++)
        {
            var p = (double)j / len;
            if (j % 32 == 0)
            {
                bl.BandPass(150 + 900 * p * p, 1.4);
                br.BandPass(160 + 950 * p * p, 1.4);
            }
            var env = Math.Pow(p, 2.2) * amp * 3;
            var k = i0 + j;
            if (k < 0 || k >= l.Length) continue;
            l[k] += (float)(bl.Run(rng.NextDouble() * 2 - 1) * env);
            r[k] += (float)(br.Run(rng.NextDouble() * 2 - 1) * env);
        }
    }

    /// <summary>A bell-like ping (a few partials, each fading at its own rate).</summary>
    private static void Bell(float[] l, float[] r, double at, double freq, double amp, double pan, double brightness)
    {
        double[] ratio = [1, 1.5, 2, 3, 4.07];
        double[] level = [1, 0.45, 0.32, 0.14 * brightness, 0.07 * brightness];
        double[] decay = [0.42, 0.3, 0.24, 0.15, 0.09];
        var len = (int)(1.6 * Rate);
        var i0 = (int)(at * Rate);
        for (var j = 0; j < len; j++)
        {
            var t = (double)j / Rate;
            double v = 0;
            for (var p = 0; p < ratio.Length; p++) v += Math.Sin(2 * Math.PI * freq * ratio[p] * t) * level[p] * Math.Exp(-t / decay[p]);
            var env = Math.Min(1, t / 0.002);
            Add(l, r, i0 + j, v * env * amp * 0.6, pan);
        }
    }

    // MARK: Mixing

    private static void Add(float[] l, float[] r, int i, double v, double pan)
    {
        if (i < 0 || i >= l.Length) return;
        var a = (pan + 1) * Math.PI / 4; // equal-power pan
        l[i] += (float)(v * Math.Cos(a) * 1.41);
        r[i] += (float)(v * Math.Sin(a) * 1.41);
    }

    /// <summary>
    /// Brings the loudest moments to full scale, then soft-limits them (the shock wave is squeezed more than the quiet
    /// parts, so it hits hard without burying the rest), and fades the very end to silence.
    /// </summary>
    private static (float[] L, float[] R) Master(float[] l, float[] r)
    {
        var peak = 1e-6f;
        for (var i = 0; i < l.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        const double drive = 2.2;
        var norm = Math.Tanh(drive);
        for (var i = 0; i < l.Length; i++)
        {
            l[i] = (float)(Math.Tanh(drive * l[i] / peak) / norm);
            r[i] = (float)(Math.Tanh(drive * r[i] / peak) / norm);
        }
        var gain = 0.7f;
        var fade = (int)(0.25 * Rate);
        for (var i = 0; i < l.Length; i++)
        {
            var g = gain * (i > l.Length - fade ? (float)(l.Length - i) / fade : 1);
            l[i] *= g;
            r[i] *= g;
        }
        return (l, r);
    }

    private static byte[] Encode((float[] L, float[] R) s)
    {
        var frames = s.L.Length;
        var data = frames * 4;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + data);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)2);          // stereo
        w.Write(Rate);
        w.Write(Rate * 4);          // bytes per second
        w.Write((short)4);          // block align
        w.Write((short)16);         // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(data);
        for (var i = 0; i < frames; i++)
        {
            w.Write((short)Math.Clamp(Math.Round(s.L[i] * 32767), -32768, 32767));
            w.Write((short)Math.Clamp(Math.Round(s.R[i] * 32767), -32768, 32767));
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>An RBJ band-pass filter (constant peak gain).</summary>
    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public void BandPass(double fc, double q)
        {
            var w0 = 2 * Math.PI * fc / Rate;
            var alpha = Math.Sin(w0) / (2 * q);
            var a0 = 1 + alpha;
            _b0 = alpha / a0;
            _b1 = 0;
            _b2 = -alpha / a0;
            _a1 = -2 * Math.Cos(w0) / a0;
            _a2 = (1 - alpha) / a0;
        }

        public void LowPass(double fc, double q)
        {
            var w0 = 2 * Math.PI * fc / Rate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0;
            _b1 = (1 - cos) / a0;
            _b2 = (1 - cos) / 2 / a0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Run(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
