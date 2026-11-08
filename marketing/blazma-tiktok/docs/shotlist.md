# Blazma Animation: TikTok cut, shot list
38.44 s · 128 bpm · 82 beats (beat 0.46875 s) · 9:16, 1080x1920, 60 fps · all text Arabic RTL
Music: `audio/score.py` (the explainer's Hijaz score re-cut to 80 beats; drop at beat 6, fall hit at beat 24, lift at 28, end hit at 72) · no VO
TikTok safe area for ALL text: x 60..960 (right UI column 120 px), y 220..1500 (top 220, bottom 420 kept clear). Footage may bleed into the unsafe zones (it is the real app UI).
Real product only: portrait recordings of the real app at 1080x1920 (UI scale 1.25; the app's narrow layout gives a tall viewport, so the character is large at 1.0x), the hero project is the saved real Kimodo result. Nothing is upscaled above 1.0x.

| # | beats | time | shot | on screen (asset) | layout / motion (new event every 1.5-2.5 s) | sound |
|---|-------|------|------|-------------------|---------------------------------------------|-------|
| 1 | 0-8 | 0.00-3.75 | Studio Nakhla intro (their own footage) | `brand-nakhla/nakhla-intro-7s.mp4`, first 3.5 s only (before the partner logos), cropped 1080x1080 at 1.0x, background extended with its own edge colour; its audio is blended into the score opening | logo reveal, hold, orange band wipes into the hook on the drop | studio audio, ney pad, riser |
| 2 | 8-14 | 3.75-6.56 | HOOK: «اكتب.. وشوف الحركة» + the hero prompt typed | recording `seq/h` 1:1 crop of the Generate dialog textbox (960x200) | beat 6 big headline slams (y 300-560), 7 dialog crop springs in, 7.5-11 prompt typed in 96 px display type under it, 10.5 caret blink | DROP, kick, typing clicks |
| 3 | 14-18 | 6.56-8.44 | PLAN | recording crop of the real plan table (960x450) | 12 card in, 13 / 13.8 / 14.6 the three step pills (في واحد وراك / اجري / موت) pop, 15 generating bar fills | pops |
| 4 | 18-30 | 8.44-14.06 | HERO: real Kimodo run then fall | recording full-bleed 1080x1920, character centred and large | 16 whip in, 16.5 caption «في واحد وراك», 20 caption «اجري», 24 BIG HIT: «وبعدها موت» slams (accent, centre-top), shake, then halftime 24-28 slow settle | whoosh, thud, impact, sub |
| 5 | 30-38 | 14.06-17.81 | FEATURE 1: free camera | `v/p10-ar-dark-camera-viewport.png` (native 1080x1920) | 28 lift cut, title «كاميرا حرة» (y 1150-1350), 31 view-menu popup, 33.5 camera-settings popup | crash, pops |
| 6 | 38-48 | 17.81-22.50 | FEATURE 2: physics balance | `v/p6-ar-dark-unbalanced-before` / `balanced-after` | 36 cut «قبل», 39 horizontal divider sweeps down, 42.5 «بعد» tag, title «توازن فيزيائي» | whoosh, bell |
| 7 | 48-58 | 22.50-27.19 | FEATURE 3: combat hit + hit test | `v/p9-ar-dark-overlay-hit`, `combo-editor`, `hit-test-dialog` | 46 cut on hitboxes + title «تصادم وضربات», 50 combo editor, 53.5 hit-test dialog card | thud, click, pop |
| 8 | 58-68 | 27.19-31.88 | FEATURE 4: AI on your device, GPU-ready | `v/reel-ar-models-installed` (real window, light tier installed), then `reel-ar-ai-settings` crop + `reel-ar-device-popup` (the 4 real device modes) | 56 dialog + title «ذكاء اصطناعي على جهازك», 60.5 device list, 61 «GPU» chip + «يدعم كرت الشاشة» (capability only; this machine has no GPU) | tick, bell, impact |
| 9 | 68-74 | 31.88-34.69 | BUTTON: the fall in slow motion | recording, 0.7x | 66 slow-mo, «حرّك شخصيتك بكلمات» in two slams | sub, pops |
| 10 | 74-82 | 34.69-38.44 | END CARD | Blazma icon, «حمّله من GitHub», repo URL, v0.12 pill, small Studio Nakhla credit (their real logo + «من استوديو نخلة») | 74 icon drops, 75.8 CTA, 77.5 version pill, 78.5 credit, hold 3 s | end hit, D major resolve, bell |

Longest gap without a new event: beats 24-28 (1.9 s, the halftime hold after the fall).


Beats shown include the 8-beat Studio Nakhla intro. The end card carries a small Studio Nakhla credit (their real logo, transparent PNG) with «من استوديو نخلة».
