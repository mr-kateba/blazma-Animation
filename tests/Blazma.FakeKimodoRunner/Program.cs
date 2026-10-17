using System.Text;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Core.Mathematics;

// Fake kimodo-runner for protocol tests. Same wire format as ai/kimodo/src/main.cpp:
// newline-delimited JSON-RPC 2.0 on stdin/stdout, logs on stderr.
// Behaviour is scripted by words in the prompt: "crash-now", "hang", "oversize", "garbage", "fail".
// Env FAKE_KIMODO_PROTOCOL overrides the protocol version reported by hello.

if (!args.Contains("--stdio"))
{
    Console.Error.WriteLine("usage: fake-kimodo-runner --stdio");
    return 2;
}

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

while (stdin.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    var msg = JsonNode.Parse(line)!.AsObject();
    var id = msg["id"];
    var p = msg["params"] as JsonObject ?? new JsonObject();
    switch ((string?)msg["method"])
    {
        case "hello":
            var proto = int.TryParse(Environment.GetEnvironmentVariable("FAKE_KIMODO_PROTOCOL"), out var pv) ? pv : 1;
            Result(id, new JsonObject { ["protocol"] = proto, ["runner"] = "fake-kimodo-runner", ["version"] = "0.0-test", ["kimodoAbi"] = 1, ["skeletons"] = new JsonArray("soma30") });
            break;
        case "diagnose":
            Result(id, new JsonObject
            {
                ["vulkanCompiled"] = false, ["ggml"] = false,
                ["motionModelExists"] = File.Exists((string?)p["motionModel"] ?? ""),
                ["warnings"] = new JsonArray("fake runner: no model is ever loaded"),
            });
            break;
        case "generate":
        {
            var prompt = (string?)p["prompt"] ?? "";
            var frames = (int?)p["frames"] ?? 30;
            Console.Error.WriteLine($"generate '{prompt}' frames={frames} seed={p["seed"]}");
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
                ["fps"] = 30, ["frames"] = frames, ["joints"] = SomaSkeleton.Names.Count,
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
}
return 0;
