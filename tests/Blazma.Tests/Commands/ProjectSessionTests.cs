using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Tests.Persistence;

namespace Blazma.Tests.Commands;

public class ProjectSessionTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();
    static readonly CommandOrigin Agent = new(CommandSource.Mcp, "agent-1");

    static (ProjectSession s, List<SessionChangedArgs> events) NewSession()
    {
        var s = new ProjectSession(Project.CreateNew("Demo"));
        var events = new List<SessionChangedArgs>();
        s.Changed += events.Add;
        return (s, events);
    }

    static CommandResult Run(ProjectSession s, string id, object args, CommandOrigin? origin = null) =>
        s.Execute(Registry.Create(id, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject()), origin ?? CommandOrigin.Ui);

    [Fact]
    public void CreateClip_ThenUndoRedo_RestoresSameClip()
    {
        var (s, _) = NewSession();
        var r = Run(s, "clip.create", new { name = "Walk", durationFrames = 48 });
        Assert.True(r.Ok);
        var id = (string)r.Data!["clipId"]!;
        Assert.Equal(48, s.Project.Clips.Single().DurationFrames);
        Assert.True(s.IsDirty);

        Assert.True(s.Undo(CommandOrigin.Ui).Ok);
        Assert.Empty(s.Project.Clips);
        Assert.False(s.IsDirty);

        Assert.True(s.Redo(CommandOrigin.Ui).Ok);
        Assert.Equal(id, s.Project.Clips.Single().Id.ToString()); // redo recreates the same id
    }

    [Fact]
    public void FailedCommand_ChangesNothing_AndIsNotUndoable()
    {
        var (s, events) = NewSession();
        var r = Run(s, "clip.rename", new { clipId = Blazma.Core.Ids.ObjectId.New().ToString(), name = "x" });
        Assert.False(r.Ok);
        Assert.Equal("not_found", r.Error!.Code);
        Assert.False(s.CanUndo);
        Assert.False(s.IsDirty);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("clip.create", "{}", "invalid_args")]
    [InlineData("clip.create", "{\"name\":\"  \"}", "invalid_args")]
    [InlineData("clip.create", "{\"name\":\"a\",\"durationFrames\":0}", "invalid_args")]
    [InlineData("clip.create", "{\"name\":\"a\",\"durationFrames\":\"ten\"}", "invalid_args")]
    [InlineData("clip.create", "{\"name\":\"a\",\"bogus\":1}", "invalid_args")]
    [InlineData("project.set_frame_rate", "{\"numerator\":5000}", "invalid_args")]
    [InlineData("nope.nope", "{}", "unknown_command")]
    public void InvalidArguments_AreRejectedWithCode(string id, string json, string code)
    {
        var e = Assert.Throws<CommandException>(() => Registry.Create(id, JsonNode.Parse(json)!.AsObject()).Execute(Project.CreateNew("x")));
        Assert.Equal(code, e.Code);
    }

    [Fact]
    public void Transaction_GroupsCommandsIntoOneUndoStep_AndMergesRenames()
    {
        var (s, _) = NewSession();
        var id = (string)Run(s, "clip.create", new { name = "A" }).Data!["clipId"]!;
        using (s.BeginTransaction("Type name", CommandOrigin.Ui))
        {
            foreach (var n in new[] { "W", "Wa", "Wal", "Walk" })
                Assert.True(Run(s, "clip.rename", new { clipId = id, name = n }).Ok);
        }
        Assert.Equal("Walk", s.Project.Clips[0].Name);
        Assert.Equal(2, s.UndoHistory.Count);
        Assert.Equal(1, s.UndoHistory[^1].CommandCount); // four renames merged

        s.Undo(CommandOrigin.Ui);
        Assert.Equal("A", s.Project.Clips[0].Name);
        s.Redo(CommandOrigin.Ui);
        Assert.Equal("Walk", s.Project.Clips[0].Name);
    }

    [Fact]
    public void TransactionRollback_RevertsEverything()
    {
        var (s, _) = NewSession();
        var tx = s.BeginTransaction("Agent task", Agent);
        Run(s, "clip.create", new { name = "A" }, Agent);
        Run(s, "project.rename", new { name = "Changed" }, Agent);
        tx.Rollback();
        Assert.Empty(s.Project.Clips);
        Assert.Equal("Demo", s.Project.Name);
        Assert.False(s.CanUndo);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void OtherOrigin_IsRejectedWhileTransactionOpen()
    {
        var (s, _) = NewSession();
        using var tx = s.BeginTransaction("Drag", CommandOrigin.Ui);
        var r = Run(s, "project.rename", new { name = "x" }, Agent);
        Assert.Equal("busy", r.Error!.Code);
    }

    [Fact]
    public void Agent_MustNameTransaction_AndCannotUndoUserWork()
    {
        var (s, _) = NewSession();
        var agentTx = Run(s, "clip.create", new { name = "Agent clip" }, Agent).TransactionId!;
        Run(s, "project.rename", new { name = "User rename" });

        Assert.Equal("transaction_required", s.Undo(Agent).Error!.Code);
        Assert.Equal("not_latest", s.Undo(Agent, agentTx).Error!.Code);
        Assert.Equal("User rename", s.Project.Name);

        s.Undo(CommandOrigin.Ui); // user undoes own rename
        Assert.True(s.Undo(Agent, agentTx).Ok);
        Assert.Empty(s.Project.Clips);
    }

    [Fact]
    public void DirtyFlag_TracksSavePoint()
    {
        var (s, events) = NewSession();
        Run(s, "project.rename", new { name = "One" });
        s.MarkSaved("/tmp/x.blazanim");
        Assert.False(s.IsDirty);
        Run(s, "project.rename", new { name = "Two" });
        Assert.True(s.IsDirty);
        s.Undo(CommandOrigin.Ui);
        Assert.False(s.IsDirty); // back at the saved state
        s.Undo(CommandOrigin.Ui);
        Assert.True(s.IsDirty);
        Assert.Contains(events, e => e.Changes.Any(c => c.Kind == Change.SaveStateChanged));
    }

    [Fact]
    public void ReadOnlyCommand_DoesNotTouchHistory()
    {
        var (s, events) = NewSession();
        Run(s, "clip.create", new { name = "Walk", durationFrames = 30 });
        events.Clear();
        var r = Run(s, "project.inspect", new { });
        Assert.True(r.Ok);
        Assert.Equal("Walk", (string)r.Data!["clips"]![0]!["name"]!);
        Assert.Single(s.UndoHistory);
        Assert.Empty(events);
    }

    [Fact]
    public void DeleteClip_UndoRestoresPosition()
    {
        var (s, _) = NewSession();
        var ids = new[] { "A", "B", "C" }.Select(n => (string)Run(s, "clip.create", new { name = n }).Data!["clipId"]!).ToArray();
        Run(s, "clip.delete", new { clipId = ids[1] });
        Assert.Equal(new[] { "A", "C" }, s.Project.Clips.Select(c => c.Name));
        s.Undo(CommandOrigin.Ui);
        Assert.Equal(new[] { "A", "B", "C" }, s.Project.Clips.Select(c => c.Name));
    }

    [Fact]
    public void Registry_ExposesToolNamesAndSchemas()
    {
        var create = Registry.All.Single(d => d.Id == "clip.create");
        Assert.Equal("clip_create", create.ToolName);
        Assert.Equal("object", (string)create.InputSchema["type"]!);
        Assert.Equal(CommandEffect.Destructive, Registry.All.Single(d => d.Id == "clip.delete").Effect);
    }

    [Fact]
    public void OpLog_RecordsOriginAndResult()
    {
        using var dir = new TempDir();
        var log = new JsonlOpLog(dir.File("oplog.jsonl"));
        var s = new ProjectSession(Project.CreateNew("x"), null, log);
        Run(s, "project.rename", new { name = "y" }, Agent);
        Run(s, "clip.delete", new { clipId = Blazma.Core.Ids.ObjectId.New().ToString() });
        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"origin\":\"mcp:agent-1\"", lines[0]);
        Assert.Contains("\"error\":\"not_found\"", lines[1]);
    }

    /// <summary>Phase 1 acceptance (core part): edit → save → crash → recover → nothing lost.</summary>
    [Fact]
    public void Acceptance_EditSaveCrashRecover_LosesNothing()
    {
        using var dir = new TempDir();
        var path = dir.File("demo.blazanim");
        var store = new RecoveryStore(dir.File("recovery"));

        var s = new ProjectSession(Project.CreateNew("Demo"));
        var recovery = store.BeginSession(s.Project, null);
        Run(s, "clip.create", new { name = "Walk", durationFrames = 48 });
        ProjectFile.Save(s.Project, path);
        s.MarkSaved(path);
        recovery.MarkSaved(path, s.Project);

        // More work after the save, captured only by autosave.
        Run(s, "clip.create", new { name = "Run", durationFrames = 24 });
        Run(s, "project.rename", new { name = "Demo v2" });
        System.Threading.Thread.Sleep(20);
        recovery.Autosave(s.Project);

        // Simulated crash: the process dies without EndCleanly().
        var marker = System.IO.Path.Combine(recovery.Folder, "session.json");
        var json = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
        json["pid"] = 999_999_999;
        File.WriteAllText(marker, json.ToJsonString());

        var candidate = Assert.Single(store.FindRecoverable());
        Assert.Equal(System.IO.Path.GetFullPath(path), candidate.ProjectPath);
        var recovered = store.Recover(candidate);
        Assert.Equal("Demo v2", recovered.Name);
        Assert.Equal(new[] { "Walk", "Run" }, recovered.Clips.Select(c => c.Name));
        Assert.Equal("Demo", ProjectFile.Load(path).Name); // the user's file was never overwritten by autosave
    }
}
