using System.Text.Json.Nodes;

namespace Blazma.AgentTools;

/// <summary>A tool that is not a registry command (project/task/undo plumbing).</summary>
public sealed record SessionTool(string Name, string Description, string Method, JsonObject Schema, bool HeadlessOnly);

/// <summary>The fixed set of session tools; the command tools are always read from the registry.</summary>
public static class SessionTools
{
    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false,
        };
    }

    public static readonly IReadOnlyList<SessionTool> All = new SessionTool[]
    {
        new("project_info", "Name, file path and unsaved-changes flag of the open project. Call this first. Effect: readonly.",
            "project.info", Schema(), false),
        new("project_save", "Save the project to its file (atomic, with read-back check), or to 'path' (save as). Effect: external (writes a file).",
            "project.save", Schema(("path", "string", "Optional new file path (.blazanim).", false)), false),
        new("task_begin", "Start a named task. All edits until task_finish are grouped, shown to the user as one unit and can be approved or rejected together. Effect: session.",
            "task.begin", Schema(("title", "string", "Short description of the work, e.g. \"Heavier attack\".", true)), false),
        new("task_finish", "End the current task. In the app's Assisted mode the user then reviews it (Approve/Reject). Effect: session.",
            "task.finish", Schema(("summary", "string", "What was done, one or two sentences.", false)), false),
        new("task_status", "Your recent tasks with their state (running, review, approved, rejected) and the number of pending approvals. Effect: readonly.",
            "task.status", Schema(), false),
        new("edit_undo", "Undo one of your own changes. Only the most recent change on the undo stack can be undone, by passing the transactionId its tool call returned. Effect: mutating.",
            "edit.undo", Schema(("transactionId", "string", "transactionId returned by the edit to undo.", true)), false),
        new("project_new", "Headless only: create a new .blazanim project containing the default mannequin character, save it and open it. Fails if the file exists. Effect: external (creates a file).",
            "project.new", Schema(("path", "string", "File path for the new project, e.g. C:\\anim\\demo.blazanim.", true),
                                  ("name", "string", "Project name (default: file name).", false),
                                  ("discard", "boolean", "Discard unsaved changes of the currently open project.", false)), true),
        new("project_open", "Headless only: open an existing .blazanim project (takes its session lock; fails if another process has it open). Effect: external (reads a file).",
            "project.open", Schema(("path", "string", "Path of the project file.", true),
                                   ("discard", "boolean", "Discard unsaved changes of the currently open project.", false)), true),
        new("project_close", "Headless only: close the open project and release its lock. Effect: session.",
            "project.close", Schema(("discard", "boolean", "Discard unsaved changes.", false)), true),
    };
}
