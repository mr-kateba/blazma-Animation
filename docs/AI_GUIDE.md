# Blazma Animation: guide for AI agents

You control Blazma Animation through MCP tools (`blazma-mcp`) or the command line (`blazma-anim`). Both expose the same commands. The full lists are generated from the code: [MCP_TOOLS.md](MCP_TOOLS.md) and [CLI_REFERENCE.md](CLI_REFERENCE.md). Step-by-step sequences: [AGENT_RECIPES.md](AGENT_RECIPES.md).

## Concepts

- **Project** (`.blazanim`): rigs, characters, clips, a pose library. Saved atomically; a `.lock` file stops two processes from editing one project.
- **Character**: a rig instance in the scene. New projects contain the default mannequin (1.80 m humanoid, bones such as `pelvis`, `spine_01..03`, `head`, `upper_arm_l`, `lower_arm_l`, `thigh_l`, `calf_l`, `foot_l`; `_l` = the character's left, `_r` = right).
- **Working pose**: each character has a current pose. `pose_set_bone`, `pose_set_control`, `pose_reset`, `pose_apply`, `poselib_apply` and `anim_pose_from_frame` change it. It is not animation until you key it.
- **Clip**: an animation (`clip_create`), measured in integer frames (default 30 fps). Frame 0 is the first frame; a clip with `durationFrames: 25` has frames 0..24.
- **Key**: `key_set {clipId, characterId, frame}` records the character's working pose at a frame (every bone and IK control unless you pass `bones`). Interpolation defaults to cubic; `key_set_interpolation` changes it (step, linear, cubic).
- **IK**: each limb has an IK target (`ik_foot_l`, `ik_hand_r`, ...) and a pole (`pole_knee_l`, ...). `pose_set_control` moves a target and solves the chain; `pose_set_pinned` keeps a target fixed while the body moves.
- **Markers and phases**: `marker_add`, `clip_add_phase_markers` label the key poses (initial, anticipation, main_action, contact, follow_through, recovery, final).
- **Task**: a named group of edits (`task_begin` ... `task_finish`). The user approves or rejects it as a unit.
- **Transaction**: every mutating call returns a `transactionId`. `edit_undo {transactionId}` undoes your own latest change.

## Conventions

| Quantity | Convention |
|---|---|
| Rotations | **degrees**, intrinsic **Euler XYZ**, local to the parent bone (`R = Rx * Ry * Rz`, so Z is applied first) |
| Positions | **metres** |
| Axes | **+Y up, +Z forward, +X = the character's left** |
| Time | integer frames; `frame` arguments are whole numbers (`anim_evaluate` accepts fractions) |
| Ids | opaque strings returned by earlier calls (`characterId`, `clipId`, `markerId`, ...); bones and controls also accept names |

Mannequin rest pose is a T-pose with identity rotations, so useful rules of thumb are:

- Leg bones hang along -Y. `thigh_* rx < 0` swings the leg forward (+Z); `rx > 0` swings it back. `calf_* rx > 0` bends the knee.
- Arms point along +X (left) or -X (right). Lower them with `upper_arm_l rz = -80` / `upper_arm_r rz = +80`; with the arm hanging, `rx < 0` swings it forward.
- With the arm still horizontal, `upper_arm_r ry > 0` swings it forward (left arm: `ry < 0`).
- `elbow` hinges: `lower_arm_l ry` negative folds the forearm forward, `lower_arm_r ry` positive.
- Joint limits apply (knees and elbows are hinges); out-of-range values are clamped and reported in `warnings`.

Always check your assumptions: `pose_get` / `anim_evaluate` return model-space bone positions in metres, so you can verify that a hand is above the head or a foot is in front of the pelvis instead of guessing.

## Workflow

1. `project_info` (headless: `project_new` or `project_open` first), then `project_inspect` for character, rig and clip ids. `rig_inspect` lists bones, IK controls and chains.
2. `task_begin {title}`.
3. Build poses with `pose_set_bone` / `pose_set_control`, record with `key_set`. Read back with `anim_evaluate`.
4. Restructure with `key_move`, `key_copy` / `key_paste`, `key_set_interpolation`, `key_set_value`, `key_set_tangents`, `clip_set_duration`, `clip_set_loop`.
5. `task_finish {summary}`, then `project_save`.

Tips:

- Prefer few, meaningful key poses over dense keys; add in-betweens only where needed.
- Make one change at a time and verify it. Results are small JSON documents; `ok: false` always includes `error.code` and a message that says what to fix.
- `key_move` replaces keys it lands on. Move the latest keys first when shifting a block later.
- A failed call changes nothing.

## Text to motion (optional)

- `motion_providers` says what is installed. Kimodo (local AI, body only: no fingers or face) works only after the user installed `kimodo-runner` and the model files; nothing is downloaded automatically. `provider: "procedural"` is a built-in keyword demo (walk, run, jump, wave, squat, idle), **not AI**.
- `motion_generate {characterId, prompt, durationSeconds}` starts a background job and returns `jobId`; it does not change the project. Without Kimodo it fails with `provider_unavailable` (do not retry; tell the user, or use the procedural demo if that is what they want).
- Poll `motion_job_status {jobId}` (headless: add `waitSeconds`). When `state` is `ready`, `motion_job_apply {jobId}` adds a **draft** clip (undoable) and returns `clipId`. Read the job's `warnings` (bones that are not driven).
- Review and edit it like any clip (`anim_evaluate`, `key_set`, ...), then `clip_accept_draft {clipId}`; to discard it use `clip_delete`.

## Permissions and modes

| Effect | Assisted mode (default in the app) | Autonomous mode | Headless |
|---|---|---|---|
| readonly | runs | runs | runs |
| mutating | runs; the task waits for the user's Approve/Reject | runs; the task is accepted when finished | runs |
| destructive (`clip_delete`, `character_remove`, `poselib_delete`) | needs the user's approval | needs the user's approval | runs |
| external (`rig_import`: reads a file) | approval unless the path is in an allowed folder | same | runs |

When a call returns `approval_required` (`isError` in MCP, exit code 1 in the CLI) the action has **not** happened. Tell the user to approve it in the AI Workspace panel, and continue with other work. Do not loop on retries.

You cannot run system commands or read arbitrary files through these tools. Files are only touched by `project_*`, `rig_import` and the CLI file argument.

## Errors you will meet

| Code | Meaning |
|---|---|
| `invalid_args` | Missing/unknown/ill-typed argument, or an id that is not valid. |
| `not_found` | The clip, bone, control or character does not exist. |
| `approval_required` | The user must approve in the app. |
| `no_project` | Headless: call `project_new` / `project_open` first. |
| `project_locked` | Another process (the app, another agent) has the project open. |
| `unsaved_changes` | Headless lifecycle call would drop edits: save first or pass `discard: true`. |
| `file_exists` | `project_new` never overwrites. |
| `not_latest`, `not_owner` | `edit_undo` only undoes your own newest change. |

## CLI quick reference

```
blazma-anim new demo.blazanim --name Demo
blazma-anim inspect demo.blazanim --json
blazma-anim exec demo.blazanim pose.set_bone --arg characterId=ID --arg bone=head --arg rx=20 --arg ry=0 --arg rz=0
blazma-anim run demo.blazanim script.jsonl
```

`run` scripts are JSON lines. `${character}` is the first character, `${clip}` the last clip, and `"save":{"name":"field"}` captures a result field as `${name}`. Exit codes: 0 ok, 1 error or failed command, 3 internal error, 130 cancelled. See [CLI_REFERENCE.md](CLI_REFERENCE.md).
