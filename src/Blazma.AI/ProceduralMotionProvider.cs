using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Built-in, deterministic motion generator for tests and offline demos. <b>Not AI</b>: it recognises a few
/// keywords (walk, run, jump, wave, squat, idle; English and Arabic) and builds simple procedural cycles on the
/// SOMA skeleton, so it exercises exactly the same conversion/retarget path as Kimodo. The same prompt, duration,
/// fps and seed always give the same motion. Unknown prompts fail with <c>unsupported_prompt</c>.
/// </summary>
public sealed partial class ProceduralMotionProvider : IMotionGenerationProvider
{
    public const string ProviderId = "procedural";

    enum Kind { Walk, Run, Jump, Wave, Squat, Idle, Attack, Die, Fall, LookBack, TurnAround, Stop }

    /// <summary>Actions added for the prompt planner (D45): English words match whole words only, Arabic as substrings.</summary>
    static readonly (Kind Kind, string[] Words)[] Actions =
    {
        (Kind.Die, new[] { "die", "dies", "dying", "died", "dead", "يموت", "موت", "مات" }),
        (Kind.Fall, new[] { "fall", "falls", "falling", "fell", "collapse", "collapses", "يقع", "يطيح", "يسقط", "سقط" }),
        (Kind.LookBack, new[] { "look back", "looks back", "looking back", "over their shoulder", "over his shoulder", "يلتفت", "التفت" }),
        (Kind.TurnAround, new[] { "turn around", "turns around", "turning around", "turn", "turns", "يستدير", "يلف" }),
    };

    static readonly (Kind Kind, string[] Words)[] LateActions =
    {
        (Kind.Stop, new[] { "stop", "stops", "halt", "halts", "توقف", "وقف" }),
        (Kind.Attack, new[] { "punch", "punches", "punching", "لكمه", "لكمة" }),
        (Kind.Squat, new[] { "sit", "sits", "sit down", "sits down" }),
    };

    static readonly (Kind Kind, string[] Words)[] Keywords =
    {
        (Kind.Run, new[] { "run", "jog", "sprint", "ركض", "يركض", "جري", "يجري" }),
        (Kind.Walk, new[] { "walk", "stroll", "step", "مشي", "يمشي", "تمشي" }),
        (Kind.Jump, new[] { "jump", "hop", "leap", "قفز", "يقفز", "قفزة" }),
        (Kind.Wave, new[] { "wave", "waving", "greet", "hello", "تلويح", "يلوح", "يلوّح", "تحية" }),
        (Kind.Attack, new[] { "attack", "swing", "slash", "strike", "هجوم", "يهاجم", "ضربة", "يضرب" }),
        (Kind.Squat, new[] { "squat", "crouch", "قرفصاء", "يجلس", "قرفص" }),
        (Kind.Idle, new[] { "idle", "stand", "breath", "wait", "وقوف", "يقف", "انتظار", "ثبات" }),
    };

    public static IReadOnlyList<string> SupportedKeywords => new[] { "walk", "run", "jump", "wave", "squat", "idle", "attack", "punch", "turn around", "look back", "fall", "die", "stop" };

    public string Id => ProviderId;

    public ProviderInfo Describe() => new(Id, "Procedural (built-in, not AI)", "procedural", true, null,
        "Part of Blazma (no model, no weights).", new[] { SomaSkeleton.Key }, 0,
        new[]
        {
            "Deterministic keyword-based cycles for tests and offline demos; it does not understand free text.",
            "Keywords: " + string.Join(", ", SupportedKeywords) + " (also Arabic: مشي، ركض، قفز، تلويح، قرفصاء، وقوف، هجوم، يلتفت، يلف، يقع، يموت، وقف).",
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
        static bool Has(string p, string w) => w.Any(c => c > 127)
            ? p.Contains(w, StringComparison.Ordinal)
            : System.Text.RegularExpressions.Regex.IsMatch(p, @"(?<![a-z])" + System.Text.RegularExpressions.Regex.Escape(w) + "(?![a-z])");
        foreach (var (kind, words) in Actions)
            if (words.Any(w => Has(p, w))) return kind;
        foreach (var (kind, words) in Keywords)
            if (words.Any(w => p.Contains(w, StringComparison.Ordinal))) return kind;
        foreach (var (kind, words) in LateActions)
            if (words.Any(w => Has(p, w))) return kind;
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

    /// <summary>One pose. <see cref="Root"/>: X/Z = hips position, Y = height above ground contact (0 = the lowest ankle stands
    /// on the ground; jump flight adds to it), unless <see cref="Absolute"/>.</summary>
    sealed class Frame
    {
        public readonly Quat[] R = Enumerable.Repeat(Quat.Identity, SomaSkeleton.Names.Count).ToArray();
        public Vec3 Root;

        /// <summary><see cref="Root"/> is the final hips position (gait cycles and idle place their feet themselves).</summary>
        public bool Absolute;
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
        var gait = kind switch
        {
            Kind.Walk => PlanGait(WalkGait, duration, loop, phase0, vary),
            Kind.Run => PlanGait(RunGait, duration, loop, phase0, vary),
            _ => null,
        };
        for (var i = 0; i < n; i++)
        {
            var t = i / fps;
            var f = new Frame();
            switch (kind)
            {
                case Kind.Walk or Kind.Run: GaitFrame(f, gait!, t); break;
                case Kind.Jump: Jump(f, t, duration, vary); break;
                case Kind.Wave: Wave(f, t, vary, phase0); break;
                case Kind.Squat: Squat(f, t / duration, vary); break;
                case Kind.Attack: Attack(f, t, t / duration, vary, phase0); break;
                case Kind.LookBack: LookBack(f, t, t / duration, vary, phase0); break;
                case Kind.TurnAround: TurnAround(f, t, t / duration, vary, phase0); break;
                case Kind.Fall or Kind.Die: Fall(f, t, t / duration, kind == Kind.Die, vary); break;
                default: PlantedIdle(f, t, vary, phase0, loop ? duration : 0); break;
            }
            if (f.Absolute)
            {
                if (inPlace) f.Root = f.Root with { Z = 0 }; // the cycles travel at a constant speed: in place keeps bob and sway
                Array.Copy(f.R, 0, rot, i * SomaSkeleton.Names.Count, SomaSkeleton.Names.Count);
                root[i] = f.Root;
                continue;
            }
            // Stand the lowest ankle on the ground.
            var (al, ar) = Ankles(f);
            f.Root = f.Root with { Y = AnkleHeight - Math.Min(al.Y, ar.Y) + f.Root.Y };
            if (inPlace) f.Root = f.Root with { X = 0, Z = 0 };
            Array.Copy(f.R, 0, rot, i * SomaSkeleton.Names.Count, SomaSkeleton.Names.Count);
            root[i] = f.Root;
        }
        if (req.Constraints is { Loop: true } && kind is Kind.Jump or Kind.Squat or Kind.Wave or Kind.Attack)
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
    static void ArmsDown(Frame f, double swingL, double swingR, double elbow) => ArmsDown(f, swingL, swingR, elbow, elbow);

    static void ArmsDown(Frame f, double swingL, double swingR, double elbowL, double elbowR)
    {
        f.R[LArm] = Rx(swingL) * Rz(-80);
        f.R[RArm] = Rx(swingR) * Rz(80);
        f.R[LFore] = Ry(-elbowL);
        f.R[RFore] = Ry(elbowR);
    }

    /// <summary>Thigh/knee angles that put the ankle straight below the hip at vertical distance <paramref name="d"/>.</summary>
    static (double A, double K) LegForHeight(double d)
    {
        d = Math.Clamp(d, Math.Abs(Thigh - Shin) + 1e-3, LegLength - 1e-6);
        var a = -Math.Acos(Math.Clamp((Thigh * Thigh + d * d - Shin * Shin) / (2 * Thigh * d), -1, 1)) / Deg;
        var shinWorld = Math.Acos(Math.Clamp((Shin * Shin + d * d - Thigh * Thigh) / (2 * Shin * d), -1, 1)) / Deg;
        return (a, shinWorld - a);
    }

    static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

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

    /// <summary>Key poses of a one-handed overhead swing (right hand): (u, upper arm Rz, upper arm Ry, forearm Rz, trunk twist, crouch).</summary>
    static readonly (double U, double ArmZ, double ArmY, double Fore, double Twist, double Crouch)[] AttackKeys =
    {
        (0, 80, 0, 0, 0, 0), (0.3, -60, -35, -50, -20, 0), (0.45, 40, 55, -5, 25, 0.06), (0.6, 60, 45, -10, 20, 0.06), (1, 80, 0, 0, 0, 0),
    };

    /// <summary>Wind-up (anticipation), fast swing forward and down (main action), follow-through, recovery to idle.</summary>
    static void Attack(Frame f, double t, double u, double vary, double phase0)
    {
        u = Math.Clamp(u, 0, 1);
        var k = 1;
        while (k < AttackKeys.Length - 1 && u > AttackKeys[k].U) k++;
        var a = AttackKeys[k - 1];
        var b = AttackKeys[k];
        var s = Smooth((u - a.U) / (b.U - a.U));
        double L(double x, double y) => x + (y - x) * s;
        var crouch = L(a.Crouch, b.Crouch) * vary;
        Idle(f, t, vary, phase0);
        var (la, lk) = LegForHeight(LegLength - 0.01 - crouch);
        SetLeg(f, LLeg, LShin, LFoot, la, lk);
        SetLeg(f, RLeg, RShin, RFoot, la, lk);
        f.R[RArm] = Ry(L(a.ArmY, b.ArmY) * vary) * Rz(L(a.ArmZ, b.ArmZ));
        f.R[RFore] = Rz(L(a.Fore, b.Fore));
        var twist = L(a.Twist, b.Twist) * vary;
        f.R[Spine2] = Ry(0.5 * twist);
        f.R[Chest] = Ry(0.5 * twist);
    }

    /// <param name="loopDuration">When &gt; 0 (loop requested), breathing and sway are rounded to whole cycles over this length.</param>
    static void Idle(Frame f, double t, double vary, double phase0, double loopDuration = 0)
    {
        double Freq(double hz) => loopDuration > 0 ? Math.Max(1, Math.Round(loopDuration * hz)) / loopDuration : hz;
        var b = Math.Sin(2 * Math.PI * (Freq(0.25) * t + phase0));
        var sway = Math.Sin(2 * Math.PI * (Freq(0.1) * t + phase0));
        var (a, k) = LegForHeight(LegLength - 0.01);
        SetLeg(f, LLeg, LShin, LFoot, a, k);
        SetLeg(f, RLeg, RShin, RFoot, a, k);
        ArmsDown(f, 3 * b, 3 * b, 12);
        f.R[Chest] = Rx(-2 * b * vary);
        f.R[Neck1] = Rx(1.5 * b);
        f.Root = new Vec3(0.015 * sway, 0, 0);
    }
}
