# Marketing videos

- `blazma-explainer-1080p.mp4` — 64 s explainer (16:9, 1080p60): Studio Nakhla intro, then the 60 s film. Score: `blazma-explainer-music.mp3`.
- `blazma-tiktok-1080x1920.mp4` — 38 s TikTok cut (9:16, 1080x1920, 60 fps), poster `blazma-tiktok-poster.png`. Text stays inside TikTok's safe area (x 60-960, y 220-1500).
- `brand-nakhla/` — Studio Nakhla's own assets: the intro (`nakhla-intro-7s.mp4`, only its first 3.5-4.1 s are used, before the partner logos), the logo sources and the transparent PNGs (`nakhla-logo.png` original colours, `nakhla-logo-ondark.png` cream wordmark for dark backgrounds).
- `blazma-explainer/` and `blazma-tiktok/` — film sources (film.js, score.py, shot lists). Footage frames are not stored; re-capture them from the app with `scripts/rec2.sh` and `scripts/shots.sh`; encode with `scripts/encode2.sh`.
- `blazma-x/` — 43 s cut for X (Twitter), 1920x1080 and 1080x1080 (`x-16x9.mp4`, `x-1x1.mp4`, `x-poster.png`, 30 fps, about 11.8 MB each), NO music: Studio Nakhla intro with its own sound, a screen-recording-style demo (real pointer, real clicks, human typing), Arabic creator-style captions, foley only (-16 LUFS). Tweet text in `blazma-x/tweet.md`. Sources, shot list and critique are in the same folder; the recording is made with `scripts/rec-x.sh` + `scripts/demo-human.patch` (not applied to the app).
- `projects/hero.blazanim` — the saved hero animation used in both films («في واحد وراك، اجري، وبعدها موت»).
