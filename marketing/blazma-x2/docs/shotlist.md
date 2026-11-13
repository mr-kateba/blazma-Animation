# Blazma Animation: X cut #2, "from keyframes to one sentence"
44.6 s · 1920x1080 (16:9) and 1080x1080 (1:1) · rendered at 30 fps · H.264 + AAC · NO music · NO voice · Gulf Arabic captions

Story: an animator in a manual keyframe editor loses a whole night posing a wooden mannequin joint by joint; every keyframe
and curve he made collapses into one line, the line becomes a text field, and the sentence he wanted writes itself. The field
lands on the real prompt box of the real app, Blazma reads the sentence as three steps, and the real Kimodo result plays:
startled, runs, falls. The window opens to show the timeline full of generated keys: «ولا كي فريم واحد بيدك». Four real
feature captures, then the invitation.

The manual editor is drawn in code and deliberately generic (no product's name, logo, layout or icons): a wooden mannequin,
a rotation ring, a 90-frame timeline with joint rows, a curve view, an analogue clock, paper cups on the desk.
Everything after the turn is the real app: two screen recordings made with the app's demo mode (`BLAZMA_DEMO_HUMAN=1`), shown at
1.0x (never upscaled), plus real captures from `docs/screenshots`.

| # | film s | shot | picture | caption | sound |
|---|--------|------|---------|---------|-------|
| 1 | 0.0-3.5 | Studio Nakhla | the studio's own intro, first 3.5 s (before the partner logos); 1:1 = centre crop | - | the intro's own soft low tone |
| 2 | 3.5-6.9 | the pain, slow | cold grey editor; cursor drags a rotation ring on the elbow, then shoulder, then knee; one key lands on frame 1 each time; counter «1 / 90»; clock 9 pm | «تحرّك كل مفصل.. بيدك» | three soft wooden taps (one per key) |
| 3 | 6.9-10.0 | the pain, faster | edits speed up, keys pile up in frames 1-6 of 90, curve view slides in, first cups | «كي فريم.. ورا كي فريم» | a soft paper-cup set-down per cup |
| 4 | 10.0-13.9 | the pain, night | curves tangle, clock passes 3 am, four cups, edges darken, counter only at 12 / 90 | «ساعات.. عشان حركة ثلاث ثواني؟» | cups only |
| 5 | 13.9-17.6 | THE TURN | freeze; every key and curve flies into one orange line; the cold editor warms to Blazma's colour; the line opens into a field; the sentence writes itself word by word; the field travels onto the real prompt box | «طيب.. وإذا كتبتها بس؟» | a slow dark breath-in, three soft taps for the words, a low air whoosh as the field lands |
| 6 | 17.6-21.5 | real dialog | UI scale 1.6 take: the typed sentence, the plan with three understood steps; spotlight on the dialog, accent outline on the box then on the steps | «تكتبها بكلامك..» / «ويفهمها خطوات» / «ويولّد الحركة على جهازك» + «(وقت الانتظار مختصر)» | silence |
| 7 | 21.5-28.9 | the run | the real Kimodo result in the viewport (1.0x crop): startled, runs, falls | «في واحد وراك..» / «اجري..» / «وبعدها موت!» | a round soft pop as it starts, a deep soft thud on the landing |
| 8 | 28.9-32.0 | the reveal | the crop opens to the whole window; the timeline row full of generated keys gets an outline | «ولا كي فريم واحد بيدك» | low air whoosh |
| 9 | 32.0-40.2 | features, 2 s each | (a) the dialog's own line «Kimodo: متاح / يعمل على هذا الحاسوب» + the real device list (GPU first / GPU only / CPU only / GPU + CPU); (b) balance before (red) -> after (green); (c) free camera: viewport + view menu + camera settings; (d) the plan of the dialect sentence, every step «مفهومة» | «الذكاء الاصطناعي يشتغل على جهازك» + «بدون سحابة.. ويدعم كرت الشاشة» / «توازن وفيزياء.. حركة طبيعية» / «كاميرا حرة.. من أي زاوية» / «يفهم العربي.. حتى باللهجة» | one very soft tap per beat |
| 10 | 40.2-44.6 | call to action | Blazma icon, «تعال جرّب Blazma», «حمّله من GitHub», github.com/mr-kateba/blazma-Animation, the Nakhla logo, «من استوديو نخلة» | - | a soft two-note chime |

Rebuild (from the reel workspace): `rec-x2.sh` (UI 1.6) and `rec-x3.sh` (UI 1.0, run) record the app -> `tools/frames.sh` ->
`node blazma-x2/tools/marks.mjs` -> render 16:9 and 1:1 with the motion-reel renderer at 30 fps -> `node blazma-x2/audio/foley.mjs &&
node blazma-x2/audio/master.mjs` -> `tools/encode.sh`.
