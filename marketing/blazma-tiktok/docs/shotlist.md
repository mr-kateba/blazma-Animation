# Blazma Animation: TikTok cut, shot list
37.5 s · 128 bpm · 80 beats (beat 0.46875 s, bar 1.875 s, 20 bars) · 9:16, 1080x1920, 60 fps · all text Arabic RTL
Music: `audio/score.py` (the explainer's Hijaz score re-cut to 80 beats; drop at beat 6, fall hit at beat 24, lift at 28, end hit at 72) · no VO
TikTok safe area for ALL text: x 60..960 (right UI column 120 px), y 220..1500 (top 220, bottom 420 kept clear). Footage may bleed into the unsafe zones (it is the real app UI).
Real product only: portrait recordings of the real app at 1080x1920 (UI scale 1.25; the app's narrow layout gives a tall viewport, so the character is large at 1.0x), the hero project is the saved real Kimodo result. Nothing is upscaled above 1.0x.

| # | beats | time | shot | on screen (asset) | layout / motion (new event every 1.5-2.5 s) | sound |
|---|-------|------|------|-------------------|---------------------------------------------|-------|
| 1 | 0-6 | 0.00-2.81 | STUDIO INTRO «استوديو نخلة» (own module `studio.js`) | PLACEHOLDER: plain dark card, name in the display face, dashed frame marked «مكان الشعار». No invented logo art | beat 0 frame draws in, 0.4 name slams, 1.6 accent bar, 2.4 whip into the hook; drop-in point for the real logo | ney + oud sting, riser, darbuka roll |
| 2 | 6-12 | 2.81-5.62 | HOOK: «اكتب.. وشوف الحركة» + the hero prompt typed | recording `seq/h` 1:1 crop of the Generate dialog textbox (960x200) | beat 6 big headline slams (y 300-560), 7 dialog crop springs in, 7.5-11 prompt typed in 96 px display type under it, 10.5 caret blink | DROP, kick, typing clicks |
| 3 | 12-16 | 5.62-7.50 | PLAN | recording crop of the real plan table (960x450) | 12 card in, 13 / 13.8 / 14.6 the three step pills (في واحد وراك / اجري / موت) pop, 15 generating bar fills | pops |
| 4 | 16-28 | 7.50-13.12 | HERO: real Kimodo run then fall | recording full-bleed 1080x1920, character centred and large | 16 whip in, 16.5 caption «في واحد وراك», 20 caption «اجري», 24 BIG HIT: «وبعدها موت» slams (accent, centre-top), shake, then halftime 24-28 slow settle | whoosh, thud, impact, sub |
| 5 | 28-36 | 13.12-16.88 | FEATURE 1: free camera | `v/p10-ar-dark-camera-viewport.png` (native 1080x1920) | 28 lift cut, title «كاميرا حرة» (y 1150-1350), 31 view-menu popup, 33.5 camera-settings popup | crash, pops |
| 6 | 36-46 | 16.88-21.56 | FEATURE 2: physics balance | `v/p6-ar-dark-unbalanced-before` / `balanced-after` | 36 cut «قبل», 39 horizontal divider sweeps down, 42.5 «بعد» tag, title «توازن فيزيائي» | whoosh, bell |
| 7 | 46-56 | 21.56-26.25 | FEATURE 3: combat hit + hit test | `v/p9-ar-dark-overlay-hit`, `combo-editor`, `hit-test-dialog` | 46 cut on hitboxes + title «تصادم وضربات», 50 combo editor, 53.5 hit-test dialog card | thud, click, pop |
| 8 | 56-66 | 26.25-30.94 | FEATURE 4: AI on your device, GPU-ready | `v/reel-ar-models-installed` (real window, light tier installed), then `reel-ar-ai-settings` crop + `reel-ar-device-popup` (the 4 real device modes) | 56 dialog + title «ذكاء اصطناعي على جهازك», 60.5 device list, 61 «GPU» chip + «يدعم كرت الشاشة» (capability only; this machine has no GPU) | tick, bell, impact |
| 9 | 66-72 | 30.94-33.75 | BUTTON: the fall in slow motion | recording, 0.7x | 66 slow-mo, «حرّك شخصيتك بكلمات» in two slams | sub, pops |
| 10 | 72-80 | 33.75-37.50 | END CARD | `assets/logo.png` (Blazma icon), «حمّله من GitHub», github.com/mr-kateba/blazma-Animation, v0.12 pill, small credit «استوديو نخلة» (text placeholder, same slot as the intro logo) | 72 icon drops, 73 CTA, 74.5 version pill, 75.5 credit, hold 3 s | end hit, D major resolve, bell |

Longest gap without a new event: beats 24-28 (1.9 s, the halftime hold after the fall).
Studio intro module: `blazma-tiktok/studio.js` exports `studioIntro(ctx, u)`; replace its placeholder block with `ctx.drawImage(logo, ...)` once the real logo arrives (same 0-6 beat slot; the end-card credit reuses the same asset).
