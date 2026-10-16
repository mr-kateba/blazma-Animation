# Agent recipes

Concrete sequences using only commands that exist. Every recipe is also a runnable script in [`docs/recipes/`](recipes/), and the automated tests run all of them (`CliIntegrationTests.Recipes_RunEndToEnd`):

```
blazma-anim new demo.blazanim
blazma-anim run demo.blazanim docs/recipes/walk_cycle.jsonl
```

In the scripts `${character}` is the project's first character and `${clip}` its last clip. In MCP you do the same by copying the `characterId` from `project_inspect` and the `clipId` returned by `clip_create`. The MCP calls below are shown as `tool {args}`; the tool names are the command ids with `_` instead of `.`.

Start of every MCP session: `project_new {path, name}` (or `project_open {path}`), `project_inspect`, then `task_begin {title}`; end with `task_finish {summary}` and `project_save`.

## 1. Walk-cycle skeleton of keys

Goal: a 24-frame loop with four key poses (contact, down, contact on the other leg, up) and the first pose repeated at frame 24. Script: `recipes/walk_cycle.jsonl`.

```
clip_create {name:"Walk", durationFrames:25}                  -> clipId
# frame 0: left leg forward, right arm forward (arms lowered with rz)
pose_set_bone {characterId, bone:"thigh_l", rx:-30, ry:0, rz:0}
pose_set_bone {characterId, bone:"thigh_r", rx:25,  ry:0, rz:0}
pose_set_bone {characterId, bone:"calf_l",  rx:5,   ry:0, rz:0}
pose_set_bone {characterId, bone:"calf_r",  rx:5,   ry:0, rz:0}
pose_set_bone {characterId, bone:"upper_arm_l", rx:25,  ry:0, rz:-80}
pose_set_bone {characterId, bone:"upper_arm_r", rx:-25, ry:0, rz:80}
key_set {clipId, characterId, frame:0}
# frame 6: passing pose, left knee bent: repeat the pose_set_bone calls with
#   thigh_l -10, thigh_r 10, calf_l 60, calf_r 5, arms +-10, then key_set frame 6
# frame 12: mirror of frame 0 (thigh_l 25, thigh_r -30, arms swapped), key_set frame 12
# frame 18: mirror of frame 6, key_set frame 18
key_copy {clipId, frames:"0"}                                  -> data.keysJson
key_paste {clipId, keysJson:<keysJson>, frame:24}              (the clip grows to 25 frames if needed)
clip_set_loop {clipId, enabled:true}
```

Check: `anim_evaluate {clipId, characterId, frame:0}` should show `foot_l` with a larger z than `foot_r`. To repeat the cycle, run `recipes/walk_cycle_repeat.jsonl`: it copies frames `0,6,12,18`, pastes them at frame 24, pastes frame 0 at frame 48 and the clip becomes 49 frames.

## 2. Make an attack heavier (retime the anticipation with key_move)

Start from the light punch, `recipes/attack_light.jsonl`: keys at 0 (guard), 6 (anticipation, arm cocked back, torso wound up), 9 (strike), 18 (follow-through), 24 (recovery); `clip_add_phase_markers` labels the phases.

A heavy hit has a longer wind-up, a hard snap and a long settle. Script `recipes/attack_heavier.jsonl`:

```
key_move {clipId, frames:"24", delta:10}      # recovery   24 -> 34
key_move {clipId, frames:"18", delta:8}       # follow     18 -> 26
key_move {clipId, frames:"9",  delta:6}       # strike      9 -> 15
key_set_interpolation {clipId, interpolation:"linear", frames:"6"}   # the wind-up key leaves linearly: the strike snaps
clip_set_duration {clipId, frames:35}
anim_evaluate {clipId, characterId, frame:12}                         # check the pose mid wind-up
```

Move the **latest** keys first so a moved key never lands on one that has not moved yet (it would replace it). The anticipation key stays at frame 6, so frames 6-15 are the extended wind-up. `clip_add_phase_markers` does not return marker ids. To retime phase markers yourself, create them with `marker_add {clipId, frame, name, phase}` (it returns `markerId`) and move them with `marker_update {clipId, markerId, frame}`.

## 3. Fix a pose with IK pinning (feet must not slide)

Problem: lowering the pelvis for a crouch drags the feet with it. Plant and pin the feet first, then move the body. Script `recipes/ik_pin_feet.jsonl`:

```
clip_create {name:"Crouch", durationFrames:30}
pose_set_control {characterId, control:"ik_foot_l", x:0.09,  y:0, z:0.25}
pose_set_pinned  {characterId, control:"ik_foot_l", pinned:true}
pose_set_control {characterId, control:"ik_foot_r", x:-0.09, y:0, z:-0.05}
pose_set_pinned  {characterId, control:"ik_foot_r", pinned:true}
key_set {clipId, characterId, frame:0}
pose_set_bone {characterId, bone:"pelvis", rx:0, ry:0, rz:0, tx:0, ty:0.78, tz:0}   # body drops 17 cm
key_set {clipId, characterId, frame:15}
pose_get {characterId}      # foot_l / foot_r positions are unchanged; the knees bend instead
```

`pose_set_control` positions are model space in metres (y is height above the ground). Unpin with `pose_set_pinned {pinned:false}` when the foot should follow the body again.

## 4. Copy and paste keys

```
key_copy {clipId, frames:"0,6,12,18"}                  -> data.keysJson   (frames relative to the earliest copied frame)
key_paste {clipId, keysJson:<keysJson>, frame:24}      replaces keys at the same times, creates missing tracks, extends the clip
```

`bones:"thigh_l,thigh_r"` limits a copy to some bones. Paste into another clip by passing a different `clipId` (targets the clip's rig does not know are skipped with a warning). `recipes/walk_cycle_repeat.jsonl` shows a loop.

## 5. Key-pose phases

```
clip_create {name:"Phases", durationFrames:40}
clip_add_phase_markers {clipId}                                   # Initial, Anticipation, Main Action, Contact, Follow-through, Recovery, Final spread over the clip
marker_add {clipId, frame:30, name:"Impact", phase:"none"}        -> data.markerId
marker_update {clipId, markerId, frame:32, name:"Impact (late)"}
```

Valid `phase` values: none, initial, anticipation, main_action, contact, follow_through, recovery, final. Script: `recipes/key_pose_phases.jsonl`. The phases appear in the app's key-pose strip, so the user can jump between them.

## Undo and review

- `edit_undo {transactionId}` takes back your newest change (use the `transactionId` of the call that made it). Older changes: undo the newer ones first.
- In the app's Assisted mode `task_finish` hands the whole task to the user as Approve/Reject; a rejected task is reverted completely.
