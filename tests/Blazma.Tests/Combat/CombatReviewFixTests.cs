using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Tests.Advanced;
using Blazma.Tests.Persistence;
using static Blazma.Tests.Combat.SweptHitTests;

namespace Blazma.Tests.Combat;

/// <summary>Phase 9 review regressions: combo bake length and cancelled events, reaction duration limits, hit direction in the
/// defender's real heading, hurtboxes on deleted bones, re-timed combat ranges and the v4 → v5 event repair.</summary>
public class CombatReviewFixTests
{
    [Fact]
    public void ComboBake_EarlyCancel_BakesThePlannedLength_WithoutEventsAfterTheCancel()
    {
        var c = C9.Build();
        var s = c.S;
        var longAtk = P8.ApplyTemplate(s, "builtin.attack_swing", c.Attacker, 3.0);
        var shortAtk = P8.ApplyTemplate(s, "builtin.attack_swing", c.Attacker, 1.0);
        P8.Ok(s, "event.add", new { clipId = longAtk, frame = 5, name = "early_whoosh", kind = "sound" });
        P8.Ok(s, "event.add", new { clipId = longAtk, frame = 60, name = "late_whoosh", kind = "sound" });
        P8.Ok(s, "hitbox.add", new { clipId = longAtk, name = "Late", bone = "hand_r", radius = 0.08, activeFrames = "30-70" });
        var combo = (string)P8.Ok(s, "combo.create", new { characterId = c.Attacker, name = "Cancel" }).Data!["comboId"]!;
        P8.Ok(s, "combo.add_attack", new { comboId = combo, clipId = longAtk, input = "X", cancelStartFrame = 10, cancelEndFrame = 20 });
        P8.Ok(s, "combo.add_attack", new { comboId = combo, clipId = shortAtk, input = "X", blendFrames = 4 });
        var info = P8.Ok(s, "combo.inspect", new { comboId = combo }).Data!;
        var bake = P8.Ok(s, "combo.bake", new { comboId = combo }).Data!;
        var clip = P8.Clip(s, (string)bake["clipId"]!);
        var lastFrame = (long)info["lastFrame"]!;
        Assert.InRange(clip.DurationFrames, lastFrame - 1, lastFrame);
        Assert.DoesNotContain(clip.Events, e => e.Name == "late_whoosh");
        Assert.Contains(clip.Events, e => e.Name == "early_whoosh" && e.Time.ToFrame(clip.Fps) == 5);
        // The cancelled attack's hitbox that starts after the link never becomes active.
        Assert.DoesNotContain(clip.Combat?.Hitboxes ?? new(), h => h.Name.EndsWith("Late", StringComparison.Ordinal));
        // The sequence item stops once the next attack has blended in (link 10 + blend 4).
        var seq = ComboPlanner.Plan(s.Project, s.Project.Combos.Single(x => x.Id.ToString() == combo)).Sequence;
        Assert.Equal(14, seq.Items[0].OutFrame);
        Assert.Equal(P8.Clip(s, shortAtk).DurationFrames, seq.Items[1].OutFrame);
    }

    [Fact]
    public void ReactionDuration_OneLimitForRulesGenerationAndTheArchive()
    {
        Assert.Equal(CombatLimits.MinReactionSeconds, HitReaction.MinSeconds);
        Assert.Equal(CombatLimits.MaxReactionSeconds, HitReaction.MaxSeconds);
        var c = C9.Build();
        foreach (var d in new[] { CombatLimits.MinReactionSeconds, 8.0, CombatLimits.MaxReactionSeconds })
        {
            P8.Ok(c.S, "reaction.set_rule", new { characterId = c.Defender, name = "R" + d, durationSeconds = d });
            P8.Ok(c.S, "reaction.generate", new { characterId = c.Defender, direction = "front", durationSeconds = d });
        }
        foreach (var d in new[] { CombatLimits.MinReactionSeconds / 2, CombatLimits.MaxReactionSeconds + 1 })
        {
            Assert.Equal("invalid_args", P8.Err(P8.Run(c.S, "reaction.set_rule", new { characterId = c.Defender, name = "Bad", durationSeconds = d })));
            Assert.Equal("invalid_args", P8.Err(P8.Run(c.S, "reaction.generate", new { characterId = c.Defender, direction = "front", durationSeconds = d })));
        }
        C9.RoundTrip(c.S.Project);
    }

    [Fact]
    public void HitSide_UsesTheDefendersHeadingAtTheHit_AndVelocityRelativeToIt()
    {
        var (p, a, ra, d, rd) = TwoSingles();
        // The hitbox travels along +X at 9 m/s; the defender (turned 90° by its clip: facing +X) walks along +X at 3 m/s.
        var clip = SweptHitTests.Motion(ra, 4, Vec3.UnitY, 0, new Vec3(-0.5, 0, 0), new Vec3(0.3, 0, 0));
        clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.05 }, Transform.Identity, 4, clip.Fps) } };
        p.Clips.Add(clip);
        var walk = SweptHitTests.Motion(rd, 4, Vec3.UnitY, 0, Vec3.Zero, new Vec3(0.1, 0, 0), startDeg: 90);
        p.Clips.Add(walk);
        d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.1 }, Vec3.Zero) } };
        var hit = HitDetector.Run(p, a, clip, d, walk, new HitTestOptions { DefenderPlacement = Transform.Identity }).First!;
        Assert.Equal(2, hit.Frame);
        // It hits the defender in the back (placement facing +Z alone would have called it a hit from the right).
        Assert.Equal(HitDirection.Back, hit.From);
        Assert.Equal(1, hit.LocalDirection.Z, 3);
        Assert.Equal(6, hit.Velocity.X, 1); // 9 − 3 m/s
        Assert.Equal(0, hit.Velocity.Z, 3);
    }

    [Fact]
    public void HurtboxesOnDeletedBones_AreReportedByHitTestAndExport()
    {
        var (p, a, ra, d, rd) = TwoSingles();
        var clip = SweptHitTests.Motion(ra, 2, Vec3.UnitY, 0, Vec3.Zero, Vec3.Zero);
        clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.05 }, Transform.Identity, 2, clip.Fps) } };
        p.Clips.Add(clip);
        var gone = new Hurtbox { Name = "Ghost", Bone = ObjectId.New(), Shape = new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.1 } };
        d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.1 }, Vec3.Zero), gone } };
        var rep = HitDetector.Run(p, a, clip, d, null, new HitTestOptions { DefenderPlacement = Transform.Identity });
        Assert.Single(rep.Hits);
        Assert.Contains(rep.Warnings, w => w.Contains("'Ghost'") && w.Contains("no longer exists"));

        var c = C9.Build();
        P8.Ok(c.S, "hurtbox.generate", new { characterId = c.Attacker });
        var ch = c.S.Project.Characters.Single(x => x.Id.ToString() == c.Attacker);
        ch.Combat!.Hurtboxes[0].Bone = ObjectId.New();
        var name = ch.Combat.Hurtboxes[0].Name;
        using var dir = new TempDir();
        var r = P8.Ok(c.S, "animation.export", new { clipId = c.Attack, characterId = c.Attacker, path = dir.File("a.glb") });
        Assert.Contains(r.Warnings, w => w.Message.Contains($"'{name}'") && w.Message.Contains("no longer exists"));
    }

    [Fact]
    public void Retarget_CutsCombatRangesAtTheEndOfTheRetimedClip()
    {
        var (s, man, ces) = P8.Scene();
        var atk = P8.ApplyTemplate(s, "builtin.attack_swing", man);
        P8.Ok(s, "hitbox.add", new { clipId = atk, name = "Fist", bone = "hand_r", radius = 0.08, activeFrames = "10-12" });
        var src = P8.Clip(s, atk);
        // Ranges that run past the clip (an archive may hold them up to CombatLimits.MaxTime).
        src.Combat!.Hitboxes[0].Active = new List<ClipRange>
        {
            new() { Start = Flicks.FromFrame(10, src.Fps), End = src.Duration + Flicks.FromFrame(40, src.Fps) },
            new() { Start = src.Duration + Flicks.FromFrame(50, src.Fps), End = CombatLimits.MaxTime },
        };
        var r = P8.Ok(s, "clip.retarget", new { clipId = atk, fromCharacterId = man, toCharacterId = ces });
        var clip = P8.Clip(s, (string)r.Data!["clipId"]!);
        var range = Assert.Single(clip.Combat!.Hitboxes[0].Active);
        Assert.Equal(10, range.Start.ToFrame(clip.Fps));
        Assert.Equal(clip.Duration, range.End);
        Assert.Contains(r.Warnings, w => w.Message.Contains("'Fist'") && w.Message.Contains("cut"));
        C9.RoundTrip(s.Project);
    }

    [Fact]
    public void V4Archive_EventsBreakingTheV5Limits_AreRepairedWithWarnings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "schema-v4", "project-v4.blazanim");
        var entries = new Dictionary<string, byte[]>();
        using (var zip = ZipFile.OpenRead(path))
            foreach (var e in zip.Entries)
            {
                using var st = e.Open();
                using var b = new MemoryStream();
                st.CopyTo(b);
                entries[e.FullName] = b.ToArray();
            }
        var clipEntry = entries.Keys.Single(k => k.StartsWith("clips/", StringComparison.Ordinal));
        var clip = JsonNode.Parse(entries[clipEntry])!.AsObject();
        var events = clip["events"]!.AsArray();
        events[0]!["name"] = "";
        var data = new JsonObject();
        for (var i = 0; i < 20; i++) data["k" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)] = "v" + i;
        events[1]!["data"] = data;
        for (var i = 0; i < 1100; i++)
            events.Add(new JsonObject { ["id"] = ObjectId.New().ToString(), ["time"] = 0, ["name"] = "e" + i, ["data"] = new JsonObject() });
        entries[clipEntry] = System.Text.Encoding.UTF8.GetBytes(clip.ToJsonString());
        var root = JsonNode.Parse(entries["project.json"])!.AsObject();
        Assert.Equal(4, (int)root["schemaVersion"]!);
        root["files"]![clipEntry] = Convert.ToHexString(SHA256.HashData(entries[clipEntry])).ToLowerInvariant();
        entries["project.json"] = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (k, v) in entries)
            {
                using var st = zip.CreateEntry(k).Open();
                st.Write(v);
            }
        ms.Position = 0;

        var p = ProjectArchive.Read(ms);
        var c = Assert.Single(p.Clips);
        Assert.Equal(CombatLimits.MaxEventsPerClip, c.Events.Count);
        var first = c.Events.Single(e => e.Id.ToString() == "01J9ZZZZZZZZZZZZZZZZZZZZE1");
        Assert.Equal("event", first.Name);
        var second = c.Events.Single(e => e.Id.ToString() == "01J9ZZZZZZZZZZZZZZZZZZZZE2");
        Assert.Equal(CombatLimits.MaxPayloadEntries, second.Data.Count);
        Assert.Contains("k15", second.Data.Keys);
        Assert.DoesNotContain("k16", second.Data.Keys);
        Assert.Equal(3, p.LoadWarnings.Count);
        Assert.Contains(p.LoadWarnings, w => w.Contains("only the first 1024"));
        Assert.Contains(p.LoadWarnings, w => w.Contains("named 'event'"));
        Assert.Contains(p.LoadWarnings, w => w.Contains("payload entries"));
        // It saves as a valid v5 project.
        Assert.Empty(C9.RoundTrip(p).LoadWarnings);
    }
}
