# Critique log

## Round 1 (contact sheets every 4 beats, 16x9)
Scores: hook 7, phone readability 7, motion 7, variety 7, brand 9, sound sync 8.
Worst three:
1. First frame of three cuts blank (export dialog, MCP chain, "and more" grid, end-card icon) breaks "downbeat frame is the hit". Fix: springs start a few tenths before the beat so the new image is already on screen at the cut.
2. End card: Latin name split per word and overlapped the Arabic CTA (RTL word order). Fix: `whole` option for Latin strings; sized to one line; RTL order for the mixed Arabic/Latin CTA verified.
3. Props second card and 2x2 tiles overlapped the text column. Fix: card shifted, tiles narrowed to 560 px.
Also: sub-lines raised 46 to 52 px, hook crop tightened to the textbox, camera shot zoomed in on the character.

## Round 2 (stills across all 16 shots + sfx sync)
Scores: hook 8, readability 8, motion 8, variety 8, brand 9, sound sync 8 (52/70 hits within 20 ms of an onset, median 3.5 ms).
Worst three:
1. Hero "وبعدها موت" overlapped the fallen character. Fix: moved to the top right.
2. Walk chart tags قبل/بعد were reversed against the real (LTR) chart columns. Fix: swapped.
3. AI-models dialogs read too small (full 760x720 dialog). Fix: crop the upper part (title, progress bar, status) at 960 px wide.

## Round 3 (re-render of fixes, frames pulled from the encoded file)
See report: all categories >= 8. RTL word order, Arabic joining (whole words, direction=rtl) and glyph coverage checked on the sheets; no fallback glyphs.

# v2 (60 s, native 1080p) critique
Round 1 (contact sheet): footage was 1440x900 because the Godot movie writer uses the project viewport size, not --resolution: fixed with a temporary override.cfg (1920x1080), re-recorded at 60 fps; dialog crops re-measured; plan headline overlapped the card.
Round 2 (stills): "and more" tiles overlapped the headline; scrim too weak over the dense timeline in the combat shot. Fixed.
Round 3 (encoded file, 100% crops): hero footage, dialog text and Arabic headlines crisp, no blocking at 2.35 Mbps two-pass x264.
Scores: hook 8, phone readability 8, motion 8, variety 8, brand 9, sound sync 8, sharpness at 100% 9.
