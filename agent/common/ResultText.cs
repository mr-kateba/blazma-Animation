using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazma.AgentTools;

public static class ResultText
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Pretty JSON of the result, with a plain-language line in front when it failed.</summary>
    public static string ForAgent(JsonObject result)
    {
        var json = result.ToJsonString(Pretty);
        if (AgentConnection.IsOk(result)) return json;
        var code = (string?)result["error"]?["code"];
        var message = (string?)result["error"]?["message"];
        var head = $"ERROR {code}: {message}";
        if (code == "approval_required")
        {
            var id = (string?)result["data"]?["approvalId"];
            head += id is null
                ? "\nThis action needs the user's approval, which is only possible in the Blazma Animation window."
                : $"\nNOT DONE YET: the user must approve this action in the Blazma Animation window (AI Workspace panel, approvalId {id}). Tell the user, then continue with other work or retry after they approve. Do not repeat the call.";
        }
        return head + "\n" + json;
    }
}
