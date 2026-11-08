# Marketing videos

- `blazma-explainer-1080p.mp4` — 64 s explainer (16:9, 1080p60): Studio Nakhla intro, then the 60 s film. Score: `blazma-explainer-music.mp3`.
- `blazma-tiktok-1080x1920.mp4` — 38 s TikTok cut (9:16, 1080x1920, 60 fps), poster `blazma-tiktok-poster.png`. Text stays inside TikTok's safe area (x 60-960, y 220-1500).
- `brand-nakhla/` — Studio Nakhla's own assets: the intro (`nakhla-intro-7s.mp4`, only its first 3.5-4.1 s are used, before the partner logos), the logo sources and the transparent PNGs (`nakhla-logo.png` original colours, `nakhla-logo-ondark.png` cream wordmark for dark backgrounds).
- `blazma-explainer/` and `blazma-tiktok/` — film sources (film.js, score.py, shot lists). Footage frames are not stored; re-capture them from the app with `scripts/rec2.sh` and `scripts/shots.sh`; encode with `scripts/encode2.sh`.
- `projects/hero.blazanim` — the saved hero animation used in both films («في واحد وراك، اجري، وبعدها موت»).
