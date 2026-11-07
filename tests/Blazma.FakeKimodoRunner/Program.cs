using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Core.Mathematics;

// Fake kimodo-runner for protocol tests. Same wire format as ai/kimodo/src/main.cpp:
// newline-delimited JSON-RPC 2.0 on stdin/stdout, logs on stderr.
// Behaviour is scripted by words in the prompt: "crash-now", "hang", "oversize", "garbage", "fail", "anon-error", "huge-frames".
// Env FAKE_KIMODO_PROTOCOL overrides the protocol version reported by hello (only when started directly: Blazma passes the
// runner a minimal environment). A model folder whose name contains "diagnose-fail" makes diagnose fail after 0.5 s.
// Like the real runner, stdin is read on its own thread: end of input, or "cancel" during a generation, exits at once.
//
// "--host <modelDir>": instead of being a runner, act as a Blazma host: start this program as the runner through
// KimodoProvider, begin a generation that hangs, print "runner-pid <pid>" and wait forever (the lifetime test kills it).
//
// Devices (like the 0.3 runner): "--fake-devices cuda,vulkan,igpu" lists CUDA0 / Vulkan0 / Vulkan1 (integrated) and CPU in hello
// and answers generate with the devices used; without it hello lists none (a 0.2 runner). "--fake-fail gpu:CUDA0=oom,hybrid:Vulkan0=crash"
// makes generate on that attempt (device[:gpu]) fail: oom → generation_failed, load → model_load_failed, crash → exit.

if (args.Length >= 2 && args[0] == "--host")
{
    var settings = new KimodoSettings
    {
        RunnerPath = Environment.ProcessPath,
        RunnerArguments = new[] { Path.Combine(AppContext.BaseDirectory, "fake-kimodo-runner.dll") },
        ModelDirectory = args[1],
        StartTimeout = TimeSpan.FromSeconds(60),
        GenerateTimeout = TimeSpan.FromMinutes(10),
    };
    var provider = new KimodoProvider(settings);
    var started = new TaskCompletionSource();
    _ = provider.GenerateAsync(new MotionRequest("hang", 1, 30, 1), new HostProgress(started), CancellationToken.None);
    await started.Task;
    Console.Out.WriteLine($"runner-pid {provider.RunnerProcessId}");
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (!args.Contains("--stdio"))
{
    Console.Error.WriteLine("usage: fake-kimodo-runner --stdio");
    return 2;
}

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var fakeDevices = Arg("--fake-devices")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
var fakeFail = (Arg("--fake-fail") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
JsonObject Dev(string name, string desc, string type, string backend) =>
    new() { ["name"] = name, ["description"] = desc, ["type"] = type, ["backend"] = backend, ["memoryTotal"] = 8L << 30, ["memoryFree"] = 7L << 30 };
JsonArray? DeviceList()
{
    if (fakeDevices is null) return null;
    var list = new JsonArray();
    if (fakeDevices.Contains("cuda")) list.Add(Dev("CUDA0", "NVIDIA GeForce RTX 4070", "gpu", "CUDA"));
    if (fakeDevices.Contains("vulkan")) list.Add(Dev("Vulkan0", "NVIDIA GeForce RTX 4070", "gpu", "Vulkan"));
    if (fakeDevices.Contains("igpu")) list.Add(Dev("Vulkan1", "Intel(R) UHD Graphics 770", "igpu", "Vulkan"));
    list.Add(Dev("CPU", "Fake CPU", "cpu", "CPU"));
    return list;
}
JsonObject? FindDev(string name) => DeviceList()?.OfType<JsonObject>().FirstOrDefault(d => (string?)d["name"] == name)?.DeepClone().AsObject();

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
Console.Error.WriteLine("fake kimodo-runner ready");

void Send(JsonObject o) => stdout.WriteLine(o.ToJsonString());
void Result(JsonNode? id, JsonObject r) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = r });
void Error(JsonNode? id, string code, string message) => Send(new JsonObject
{
    ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
    ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message, ["data"] = new JsonObject { ["code"] = code } },
});
void Progress(JsonNode? id, string stage, double fraction) => Send(new JsonObject
{
    ["jsonrpc"] = "2.0", ["method"] = "progress",
    ["params"] = new JsonObject { ["id"] = id?.DeepClone(), ["stage"] = stage, ["fraction"] = fraction },
});

var queue = new BlockingCollection<string>();
var busy = 0;
var reader = new Thread(() =>
{
    while (stdin.ReadLine() is { } l)
    {
        if (l.Length == 0) continue;
        if (Volatile.Read(ref busy) == 1 && l.Contains("\"method\":\"cancel\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("cancel received during a generation: exiting");
            Environment.Exit(130);
        }
        queue.Add(l);
    }
    Console.Error.WriteLine("stdin closed: exiting");
    Environment.Exit(0);
}) { IsBackground = true };
reader.Start();

foreach (var line in queue.GetConsumingEnumerable())
{
    var msg = JsonNode.Parse(line)!.AsObject();
    var id = msg["id"];
    var p = msg["params"] as JsonObject ?? new JsonObject();
    switch ((string?)msg["method"])
    {
        case "hello":
            var proto = int.TryParse(Environment.GetEnvironmentVariable("FAKE_KIMODO_PROTOCOL"), out var pv) ? pv : 1;
            var hello = new JsonObject { ["protocol"] = proto, ["runner"] = "fake-kimodo-runner", ["version"] = "0.0-test", ["kimodoAbi"] = 1, ["skeletons"] = new JsonArray("soma30") };
            if (DeviceList() is { } devs) { hello["devices"] = devs; hello["deviceModes"] = new JsonArray("auto", "gpu", "cpu", "hybrid"); }
            Result(id, hello);
            break;
        case "diagnose":
            if (((string?)p["motionModel"] ?? "").Contains("diagnose-fail"))
            {
                Thread.Sleep(500);
                Error(id, "diagnose_failed", "simulated diagnose failure");
                break;
            }
            Result(id, new JsonObject
            {
                ["vulkanCompiled"] = false, ["ggml"] = false,
                ["motionModelExists"] = File.Exists((string?)p["motionModel"] ?? ""),
                ["inheritedSecret"] = Environment.GetEnvironmentVariable("BLAZMA_TEST_SECRET") is not null,
                ["parentPid"] = Environment.GetEnvironmentVariable("KIMODO_RUNNER_PARENT_PID"),
                ["warnings"] = new JsonArray("fake runner: no model is ever loaded"),
            });
            break;
        case "generate":
        {
            Volatile.Write(ref busy, 1);
            var prompt = (string?)p["prompt"] ?? "";
            var frames = (int?)p["frames"] ?? 30;
            var device = (string?)p["device"] ?? "auto";
            var gpuName = (string?)p["gpu"];
            Console.Error.WriteLine($"generate '{prompt}' frames={frames} seed={p["seed"]} device={device}{(gpuName is null ? "" : ":" + gpuName)}");
            JsonObject? used = null;
            if (fakeDevices is not null)
            {
                if (device is not ("auto" or "gpu" or "cpu" or "hybrid" or "vulkan")) { Error(id, "invalid_request", "device must be auto, gpu, cpu or hybrid"); break; }
                var g = device == "cpu" ? null : gpuName is not null ? FindDev(gpuName) : DeviceList()!.OfType<JsonObject>().FirstOrDefault(d => (string?)d["type"] is "gpu" or "igpu")?.DeepClone().AsObject();
                if (g is null && device is "gpu" or "hybrid") { Error(id, "device_unavailable", "no usable GPU was found"); break; }
                var mode = g is null ? "cpu" : device == "hybrid" ? "hybrid" : "gpu";
                if (fakeFail.TryGetValue(gpuName is null ? device : $"{device}:{gpuName}", out var how))
                {
                    if (how == "crash") { Console.Error.WriteLine("simulated GPU crash"); Environment.Exit(3); }
                    if (how == "load") { Error(id, "model_load_failed", "could not load the Kimodo model: text encoder: cannot allocate text component"); break; }
                    Error(id, "generation_failed", "Kimodo generation failed: GGML motion weight allocation failed (out of device memory)");
                    break;
                }
                var cpu = FindDev("CPU")!;
                used = new JsonObject { ["mode"] = mode, ["text"] = mode == "gpu" ? g!.DeepClone() : cpu.DeepClone(), ["motion"] = mode == "cpu" ? cpu : g };
            }
            Progress(id, "load_model", 0.1);
            if (prompt.Contains("crash-now"))
            {
                Console.Error.WriteLine("simulated crash: out of memory");
                Environment.Exit(3);
            }
            if (prompt.Contains("hang"))
            {
                Progress(id, "generate", 0.2);
                Thread.Sleep(Timeout.Infinite);
            }
            if (prompt.Contains("garbage")) { stdout.WriteLine("this is not json"); break; }
            if (prompt.Contains("oversize")) { stdout.WriteLine("{\"jsonrpc\":\"2.0\",\"pad\":\"" + new string('x', 3 * 1024 * 1024) + "\"}"); break; }
            if (prompt.Contains("fail")) { Error(id, "generation_failed", "simulated model error"); break; }
            if (prompt.Contains("anon-error"))
            {
                // What the real runner sends for a request it could not read (too large / not JSON): no id.
                Error(null, "invalid_request", "request too large");
                Thread.Sleep(Timeout.Infinite);
            }
            var reportedFrames = prompt.Contains("huge-frames") ? 4294967396L : frames;
            for (var i = 1; i <= 4; i++) { Thread.Sleep(10); Progress(id, "generate", 0.2 + 0.18 * i); }
            Progress(id, "decode", 0.95);

            // Rest pose; the left upper arm lowers from the T-pose by 0→60° (about +Z: -60°), the root walks +Z at 1 m/s.
            var arm = SomaSkeleton.IndexOf("LeftArm");
            var rot = new JsonArray();
            var pos = new JsonArray();
            for (var f = 0; f < frames; f++)
            {
                var a = -60.0 * f / Math.Max(1, frames - 1) * Math.PI / 180;
                for (var j = 0; j < SomaSkeleton.Names.Count; j++)
                {
                    var q = j == arm ? Quat.FromAxisAngle(Vec3.UnitZ, a) : Quat.Identity;
                    rot.Add(q.X); rot.Add(q.Y); rot.Add(q.Z); rot.Add(q.W);
                }
                pos.Add(0.0); pos.Add(0.988); pos.Add(f / 30.0);
            }
            var offsets = new JsonArray();
            for (var j = 0; j < SomaSkeleton.Names.Count; j++)
            {
                var o = SomaSkeleton.Offset(j);
                offsets.Add(new JsonArray(o.X, o.Y, o.Z));
            }
            Result(id, new JsonObject
            {
                ["fps"] = 30, ["frames"] = reportedFrames, ["joints"] = SomaSkeleton.Names.Count,
                ["skeleton"] = new JsonObject
                {
                    ["key"] = "soma30", ["up"] = "+Y", ["forward"] = "+Z",
                    ["names"] = new JsonArray(SomaSkeleton.Names.Select(n => (JsonNode?)n).ToArray()),
                    ["parents"] = new JsonArray(SomaSkeleton.Parents.Select(x => (JsonNode?)x).ToArray()),
                    ["offsets"] = offsets,
                },
                ["rotations"] = rot, ["rootPositions"] = pos,
                ["model"] = (string?)p["motionModel"], ["textModel"] = (string?)p["textModel"], ["steps"] = p["steps"]?.DeepClone(),
                ["runner"] = "fake-kimodo-runner 0.0-test",
                ["warnings"] = new JsonArray("fake motion"),
                ["device"] = used,
            });
            break;
        }
        case "cancel":
            break; // notification; a real generation cannot be interrupted, the client kills the process
        case "shutdown":
            Console.Error.WriteLine("shutdown");
            return 0;
        default:
            Error(id, "unknown_method", $"Unknown method '{msg["method"]}'.");
            break;
    }
    Volatile.Write(ref busy, 0);
}
return 0;

sealed class HostProgress(TaskCompletionSource started) : IProgress<GenerationProgress>
{
    public void Report(GenerationProgress value)
    {
        if (value.Stage == "generate") started.TrySetResult();
    }
}
