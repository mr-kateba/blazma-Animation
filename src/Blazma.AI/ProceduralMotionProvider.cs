using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Built-in, deterministic motion generator for tests and offline demos. <b>Not AI</b>: it recognises a few
/// keywords (walk, run, jump, wave, squat, idle; English and Arabic) and builds simple procedural cycles on the
/// SOMA skeleton, so it exercises exactly the same conversion/retarget path as Kimodo. The same prompt, duration,
/// fps and seed always give the same motion. Unknown prompts fail with <c>unsupported_prompt</c>.
/// </summary>
public sealed class ProceduralMotionProvider : IMotionGenerationProvider
{
    public const string ProviderId = "procedural";

    enum Kind { Walk, Run, Jump, Wave, Squat, Idle }

    static readonly (Kind Kind, string[] Words)[] Keywords =
    {
        (Kind.Run, new[] { "run", "jog", "sprint", "ركض", "يركض", "جري", "يجري" }),
        (Kind.Walk, new[] { "walk", "stroll", "step", "مشي", "يمشي", "تمشي" }),
        (Kind.Jump, new[] { "jump", "hop", "leap", "قفز", "يقفز", "قفزة" }),
        (Kind.Wave, new[] { "wave", "waving", "greet", "hello", "تلويح", "يلوح", "يلوّح", "تحية" }),
        (Kind.Squat, new[] { "squat", "crouch", "قرفصاء", "يجلس", "قرفص" }),
        (Kind.Idle, new[] { "idle", "stand", "breath", "wait", "وقوف", "يقف", "انتظار", "ثبات" }),
    };

    public static IReadOnlyList<string> SupportedKeywords => new[] { "walk", "run", "jump", "wave", "squat", "idle" };

    public string Id => ProviderId;

    public ProviderInfo Describe() => new(Id, "Procedural (built-in, not AI)", "procedural", true, null,
        "Part of Blazma (no model, no weights).", new[] { SomaSkeleton.Key }, 0,
        new[]
        {
            "Deterministic keyword-based cycles for tests and offline demos; it does not understand free text.",
            "Keywords: " + string.Join(", ", SupportedKeywords) + " (also Arabic: مشي، ركض، قفز، تلويح، قرفصاء، وقوف).",
            "Body only (30 SOMA joints), like Kimodo: fingers and face are not animated.",
        });

    public Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct) =>
        Task.FromResult(new ProviderDiagnostics(Describe(), new Dictionary<string, string?> { ["engine"] = "managed code, no external process" },
            HardwareProbe.Probe(), Array.Empty<string>()));

    public Task<MotionResult> GenerateAsync(MotionRequest request, IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        request.Validate();
        ct.ThrowIfCancellationRequested();
        var kind = Classify(request.Prompt)
            ?? throw new MotionGenerationException("unsupported_prompt",
                $"The procedural provider only understands these keywords: {string.Join(", ", SupportedKeywords)}. It is not AI; install Kimodo for free text.");
        progress?.Report(new GenerationProgress("generate", 0, $"procedural {kind.ToString().ToLowerInvariant()}"));
        var result = Generate(kind, request);
        progress?.Report(new GenerationProgress("done", 1));
        return Task.FromResult(result);
    }

    static Kind? Classify(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        foreach (var (kind, words) in Keywords)
            if (words.Any(w => p.Contains(w, StringComparison.Ordinal))) return kind;
        return null;
    }

    // ------------------------------------------------------------------ generation

    const double Deg = Math.PI / 180;
    static Quat Rx(double deg) => Quat.FromAxisAngle(Vec3.UnitX, deg * Deg);
    static Quat Ry(double deg) => Quat.FromAxisAngle(Vec3.UnitY, deg * Deg);
    static Quat Rz(double deg) => Quat.FromAxisAngle(Vec3.UnitZ, deg * Deg);

    static readonly int Hips = SomaSkeleton.IndexOf("Hips"), Spine1 = SomaSkeleton.IndexOf("Spine1"), Spine2 = SomaSkeleton.IndexOf("Spine2"),
        Chest = SomaSkeleton.IndexOf("Chest"), Neck1 = SomaSkeleton.IndexOf("Neck1"), Head = SomaSkeleton.IndexOf("Head"),
        LArm = SomaSkeleton.IndexOf("LeftArm"), LFore = SomaSkeleton.IndexOf("LeftForeArm"),
        RArm = SomaSkeleton.IndexOf("RightArm"), RFore = SomaSkeleton.IndexOf("RightForeArm"),
        LLeg = SomaSkeleton.IndexOf("LeftLeg"), LShin = SomaSkeleton.IndexOf("LeftShin"), LFoot = SomaSkeleton.IndexOf("LeftFoot"),
        RLeg = SomaSkeleton.IndexOf("RightLeg"), RShin = SomaSkeleton.IndexOf("RightShin"), RFoot = SomaSkeleton.IndexOf("RightFoot");

    // Leg geometry of the SOMA rest pose (metres).
    static readonly double Thigh = -SomaSkeleton.Offset(LShin).Y;       // hip → knee
    static readonly double Shin = -SomaSkeleton.Offset(LFoot).Y;        // knee → ankle
    static readonly double AnkleHeight = SomaSkeleton.RestPositions()[LFoot].Y; // ankle above the ground at rest
    static double LegLength => Thigh + Shin;

    /// <summary>One pose. <see cref="Root"/>: X/Z = hips position (ignored for planted cycles), Y = height above
    /// ground contact (0 = the lowest ankle stands on the ground; jump flight adds to it).</summary>
    sealed class Frame
    {
        public readonly Quat[] R = Enumerable.Repeat(Quat.Identity, SomaSkeleton.Names.Count).ToArray();
        public Vec3 Root;

        /// <summary>Cycles: which foot supports the body (the one whose thigh moves backwards).</summary>
        public bool? LeftStance;
    }

    /// <summary>Ankle positions relative to the hips joint (FK of the SOMA rest offsets with the frame's rotations).</summary>
    static (Vec3 Left, Vec3 Right) Ankles(Frame f)
    {
        var n = SomaSkeleton.Names.Count;
        var pos = new Vec3[n];
        var rot = new Quat[n];
        foreach (var j in new[] { Hips, LLeg, LShin, LFoot, RLeg, RShin, RFoot })
        {
            var p = SomaSkeleton.Parents[j];
            if (p < 0) { pos[j] = Vec3.Zero; rot[j] = f.R[j]; continue; }
            rot[j] = rot[p] * f.R[j];
            pos[j] = pos[p] + rot[p].Rotate(SomaSkeleton.Offset(j));
        }
        return (pos[LFoot], pos[RFoot]);
    }

    static MotionResult Generate(Kind kind, MotionRequest req)
    {
        var fps = req.Fps;
        var n = req.FramesAt(fps);
        var rng = new Random(unchecked((int)(req.Seed ^ (req.Seed >> 32))));
        var vary = 0.9 + 0.2 * rng.NextDouble();   // amplitude / speed variation
        var phase0 = rng.NextDouble() * 0.2;        // cycle phase offset (fraction)
        var duration = (n - 1) / fps;
        var loop = req.Constraints?.Loop == true;
        var inPlace = req.Constraints?.InPlace == true;
        var warnings = new List<string> { "Procedural motion (not AI): a simple keyword cycle for testing and demos." };

        var rot = new Quat[n * SomaSkeleton.Names.Count];
        var root = new Vec3[n];
        var planted = kind is Kind.Walk or Kind.Run;
        Vec3 travel = Vec3.Zero, prevStance = Vec3.Zero;
        var prevLeft = true;
        for (var i = 0; i < n; i++)
        {
            var t = i / fps;
            var f = new Frame();
            switch (kind)
            {
                case Kind.Walk: Cycle(f, t, duration, loop, phase0, 0.9 * vary, 25 * vary, 55, 0.8, run: false); break;
                case Kind.Run: Cycle(f, t, duration, loop, phase0, 1.35 * vary, 40 * vary, 95, 1.0, run: true); break;
                case Kind.Jump: Jump(f, t / duration, vary); break;
                case Kind.Wave: Wave(f, t, vary, phase0); break;
                case Kind.Squat: Squat(f, t / duration, vary); break;
                default: Idle(f, t, vary, phase0); break;
            }
            // Stand the lowest ankle on the ground. Cycles: move the hips so the support foot does not slide
            // (the travel speed comes from the stride, ≈1.3 m/s walking).
            var (al, ar) = Ankles(f);
            var leftDown = f.LeftStance ?? al.Y <= ar.Y;
            var stance = leftDown ? al : ar;
            var y = AnkleHeight - Math.Min(al.Y, ar.Y) + f.Root.Y;
            if (planted)
            {
                if (i > 0 && leftDown == prevLeft) travel -= new Vec3(stance.X - prevStance.X, 0, stance.Z - prevStance.Z);
                prevStance = stance;
                prevLeft = leftDown;
                f.Root = new Vec3(travel.X + f.Root.X, y, travel.Z);
            }
            else f.Root = f.Root with { Y = y };
            if (inPlace) f.Root = f.Root with { X = 0, Z = 0 };
            Array.Copy(f.R, 0, rot, i * SomaSkeleton.Names.Count, SomaSkeleton.Names.Count);
            root[i] = f.Root;
        }
        if (req.Constraints is { Loop: true } && kind is Kind.Jump or Kind.Squat or Kind.Wave)
            warnings.Add("Loop: this motion starts and ends in the same standing pose.");
        return new MotionResult(ProviderId, SomaSkeleton.Definition, fps, n, rot, root, warnings)
        {
            Metadata = new Dictionary<string, string> { ["procedural.kind"] = kind.ToString().ToLowerInvariant() },
        };
    }

    static void SetLeg(Frame f, int leg, int shin, int foot, double aDeg, double kDeg)
    {
        f.R[leg] = Rx(aDeg);
        f.R[shin] = Rx(kDeg);
        f.R[foot] = Rx(-(aDeg + kDeg)); // keep the foot level
    }

    /// <summary>Arms hanging (≈80° down from the T-pose), swung forward/back (negative = forward), elbows bent forward.</summary>
    static void ArmsDown(Frame f, double swingL, double swingR, double elbow)
    {
        f.R[LArm] = Rx(swingL) * Rz(-80);
        f.R[RArm] = Rx(swingR) * Rz(80);
        f.R[LFore] = Ry(-elbow);
        f.R[RFore] = Ry(elbow);
    }

    /// <summary>Thigh/knee angles that put the ankle straight below the hip at vertical distance <paramref name="d"/>.</summary>
    static (double A, double K) LegForHeight(double d)
    {
        d = Math.Clamp(d, Math.Abs(Thigh - Shin) + 1e-3, LegLength - 1e-6);
        var a = -Math.Acos(Math.Clamp((Thigh * Thigh + d * d - Shin * Shin) / (2 * Thigh * d), -1, 1)) / Deg;
        var shinWorld = Math.Acos(Math.Clamp((Shin * Shin + d * d - Thigh * Thigh) / (2 * Shin * d), -1, 1)) / Deg;
        return (a, shinWorld - a);
    }

    static void Cycle(Frame f, double t, double duration, bool loop, double phase0, double freq, double thighAmp, double kneeAmp, double armAmp, bool run)
    {
        if (loop && duration > 0) freq = Math.Max(1, Math.Round(duration * freq)) / duration; // whole cycles
        var phi = 2 * Math.PI * (freq * t + phase0);
        var s = Math.Sin(phi);
        var c = Math.Cos(phi);
        var aL = -thighAmp * s;
        var aR = thighAmp * s;
        // The knee folds while the leg swings forward (left: cos > 0, right: cos < 0).
        var kL = 5 + kneeAmp * Math.Pow(Math.Max(0, c), 1.5);
        var kR = 5 + kneeAmp * Math.Pow(Math.Max(0, -c), 1.5);
        if (run) { kL += 20; kR += 20; }
        SetLeg(f, LLeg, LShin, LFoot, aL, kL);
        SetLeg(f, RLeg, RShin, RFoot, aR, kR);

        // Arms swing against the legs.
        ArmsDown(f, -aL * armAmp, -aR * armAmp, run ? 80 : 15);
        f.R[Hips] = Ry(6 * s);
        f.R[Spine2] = Ry(-4 * s) * Rx(run ? 10 : 2);
        f.R[Chest] = Ry(-3 * s);

        // The lower foot stands on the ground; running adds a short flight phase.
        var flight = run ? 0.05 * Math.Max(0, Math.Sin(2 * phi)) : 0;
        f.Root = new Vec3(0.02 * s, flight, 0); // forward travel is added by the planting step
        f.LeftStance = c < 0;
    }

    static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    static void Jump(Frame f, double u, double vary)
    {
        var standD = LegLength - 1e-3;
        double drop, air = 0, armSwing;
        if (u < 0.15) { drop = 0; armSwing = 0; }
        else if (u < 0.35) { var k = Smooth((u - 0.15) / 0.2); drop = 0.25 * k; armSwing = 50 * k; }       // crouch, arms back
        else if (u < 0.42) { var k = Smooth((u - 0.35) / 0.07); drop = 0.25 * (1 - k); armSwing = 50 - 200 * k; } // push off, arms up
        else if (u < 0.62) { var k = (u - 0.42) / 0.2; drop = 0.08; air = 4 * 0.35 * vary * k * (1 - k); armSwing = -150 + 60 * k; } // flight
        else if (u < 0.72) { var k = Smooth((u - 0.62) / 0.1); drop = 0.08 + 0.17 * k; armSwing = -90 + 120 * k; } // landing
        else { var k = Smooth((u - 0.72) / 0.28); drop = 0.25 * (1 - k); armSwing = 30 * (1 - k); }   // recover
        var (a, k2) = LegForHeight(standD - drop);
        SetLeg(f, LLeg, LShin, LFoot, a, k2);
        SetLeg(f, RLeg, RShin, RFoot, a, k2);
        ArmsDown(f, armSwing, armSwing, 20);
        f.R[Spine1] = Rx(30 * drop / 0.25);
        f.Root = new Vec3(0, air, 0.04 * drop / 0.25);
    }

    static void Squat(Frame f, double u, double vary)
    {
        var depth = 0.38 * vary * Smooth(Math.Sin(Math.PI * Math.Clamp(u, 0, 1)));
        var (a, k) = LegForHeight(LegLength - 1e-3 - depth);
        SetLeg(f, LLeg, LShin, LFoot, a, k);
        SetLeg(f, RLeg, RShin, RFoot, a, k);
        var s = depth / 0.38;
        f.R[Spine1] = Rx(35 * s);
        ArmsDown(f, -75 * s, -75 * s, 5);
        f.Root = new Vec3(0, 0, -0.12 * s);
    }

    static void Wave(Frame f, double t, double vary, double phase0)
    {
        Idle(f, t, vary, phase0);
        var raise = Smooth(t / 0.5);
        f.R[RArm] = Rz(80 + (-130) * raise);                       // from hanging to ≈50° above horizontal
        f.R[RFore] = Rz(-70 * raise + 25 * raise * Math.Sin(2 * Math.PI * 2.0 * t * vary)); // forearm up, waving side to side
        f.R[Head] = Ry(-10 * raise);
    }

    static void Idle(Frame f, double t, double vary, double phase0)
    {
        var b = Math.Sin(2 * Math.PI * (0.25 * t + phase0));
        var sway = Math.Sin(2 * Math.PI * (0.1 * t + phase0));
        var (a, k) = LegForHeight(LegLength - 0.01);
        SetLeg(f, LLeg, LShin, LFoot, a, k);
        SetLeg(f, RLeg, RShin, RFoot, a, k);
        ArmsDown(f, 3 * b, 3 * b, 12);
        f.R[Chest] = Rx(-2 * b * vary);
        f.R[Neck1] = Rx(1.5 * b);
        f.Root = new Vec3(0.015 * sway, 0, 0);
    }
}
