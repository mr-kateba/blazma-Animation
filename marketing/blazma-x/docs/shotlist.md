# Blazma Animation: X cut, shot list
43.08 s · 1920x1080 (16:9) and 1080x1080 (1:1) · 30 fps · H.264 two-pass, AAC · NO music · NO voice-over · captions only in Arabic (Gulf-friendly, short)
Look: a real person's screen recording, hand-edited. The recording is the real app (Godot build, 1920x1080, UI scale 1.25) driven by `BLAZMA_DEMO_HUMAN=1`: visible mouse pointer with eased, slightly curved, overshooting paths; real mouse clicks on «توليد حركة…» and Play; the prompt typed with an uneven rhythm, a pause after each word and one slip («اجرو») that is noticed and backspaced. The Kimodo generation wait is cut (the saved real result `projects/hero.blazanim` is opened; the film says so). Footage is never scaled above 1.0x (the 1:1 cut is a 1080 px crop of the same pixels). Zooms are slow zoom-outs only. Captions: one style (display face, cream on a dark rounded box), bottom-centred, quick fade.

| # | film s | shot | on screen (real) | caption | sound |
|---|--------|------|------------------|---------|-------|
| 1 | 0.00-3.50 | Studio Nakhla intro | `brand-nakhla/nakhla-intro-7s.mp4`, first 3.5 s (before the partner logos), native 1080p; 1:1 crops the centre 1080 px | none | the intro's own sound effect (no music) |
| 2 | 3.50-5.30 | HOOK: the result first | recording, the character kneels then lands (the hero run, Kimodo) | «كتبت جملة بس.. والشخصية تحرّكت!» | body-fall thud on the landing |
| 3 | 5.30-9.66 | open the dialog | recording: pointer glides to «توليد حركة…», real click, dialog opens, pointer to the prompt box | «افتح «توليد حركة»» | whoosh on the cut, mouse click x2 |
| 4 | 9.66-15.66 | type the prompt | recording at 1.2x: human typing of «في واحد وراك، اجري، وبعدها موت», one slip + backspace | «واكتب اللي تبيه بالعربي» / «غلطت؟ عادي.. تصلحها» / «تكتب بلهجتك عادي» | one key sound per real keystroke, backspace, spaces |
| 5 | 15.66-18.42 | plan | recording: the real plan table (3 steps) | «يفهم كلامك ويقسّمه خطوات» | three soft ticks as the rows appear |
| 6 | 18.42-19.88 | press Generate | recording: pointer to «توليد», click | «وتضغط «توليد»» (top, so it does not cover the button) | tick |
| 7 | 19.88-21.28 | wait cut | recording 2x: project opens, pointer to Play | «اختصرنا لك وقت التوليد» | whoosh |
| 8 | 21.28-29.18 | RUN and FALL | recording 1.0x: real click on Play, the Kimodo clip plays: startled, runs, falls | «في واحد وراك..» / «اجري..» / «وبعدها موت!» | mouse click, footsteps (every 0.4 s, alternating sides), thud on the landing |
| 9 | 29.18-31.68 | free camera | `p10-ar-dark-camera-viewport` + view menu + settings (native) | «كاميرا حرة.. من أي زاوية تبي» | whoosh, two ticks |
| 10 | 31.68-34.18 | balance | `p6` unbalanced-before, hard cut to balanced-after | «لو ما تتوازن.. تنهار» / «ومع التوازن.. تقف على رجولها» | whoosh, tick |
| 11 | 34.18-36.68 | combat | `p9` overlay-hit, then the hit-test dialog card | «ضربات وتصادم.. وتختبر الإصابة» | whoosh, tick |
| 12 | 36.68-39.48 | AI on your device | real models window (`reel-ar-models-installed`), then the real AI settings crop + device list popup | «الذكاء الاصطناعي يشتغل على جهازك» / «ويدعم كرت الشاشة» | whoosh, three ticks (capability only; this machine has no GPU, nothing simulated) |
| 13 | 39.48-43.08 | end card | Blazma icon (native 1024 master from `make-icon.py`), «حمّله من GitHub», github.com/mr-kateba/blazma-Animation, «من استوديو نخلة» with the real transparent logo | none | whoosh, soft done chime |

Audio: intro sound + foley synthesized in code (`audio/foley.mjs`), mastered to -16 LUFS integrated, true peak about -1.2 dBTP after AAC (`audio/master.mjs`). Timeline, captions and every sound come from `tools/edit.mjs` (marks measured from the recording by `tools/analyze.py`).

Rebuild: `scripts/rec-x.sh` (record) → `python3 -I blazma-x/tools/analyze.py` → convert frames to 30 fps JPEG (assets/seq/r, numbered from 76 for 2.5 s) and the intro to JPEG (assets/seq/n) → `node blazma-x/tools/edit.mjs` → render both formats with the motion-reel renderer at 30 fps → `node blazma-x/audio/foley.mjs && node blazma-x/audio/master.mjs` → `blazma-x/tools/encode.sh`.
