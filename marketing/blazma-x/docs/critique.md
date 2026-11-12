# Critique loop (3 rounds; scores 1-10: hook / muted readability / motion / variety / brand / sound sync)

## Round 1: contact sheet + phone sheet + stills of the first full render
Scores 7 / 7 / 8 / 7 / 8 / n.a.
Worst three: (1) captions too small on the phone sheet (66 px), (2) the «وتضغط توليد» caption sat on top of the dialog's Generate button the pointer was aiming at, (3) the AI shot was two small real cards floating on empty dark.
Fixed: caption size 74 px (16:9) / 66 px (1:1); that one caption moves to the top; the AI shot now sits on the real camera capture dimmed behind the cards; foley levels rebalanced (fall was 8-10 dB above the typing, now typing and clicks are clearly audible) and the master ceiling lowered so the AAC file stays under -1 dBTP.

## Round 2: contact sheets of the encoded 16:9 and 1:1 files + audio levels per second
Scores 8 / 8 / 8 / 8 / 9 / 8
Worst three: (1) the fall sound landed about 3 frames after the body touches the floor, (2) the 1:1 caption box could reach 58 px from the edge on a two-line caption (safe margin 72 px), (3) poster still not chosen.
Fixed: fall sound 50 ms earlier; 1:1 caption width capped so the box stays 88 px or more from the edges; poster = the delivered frame at 28.6 s («وبعدها موت!» with the body on the floor).

## Round 3: final files (frames pulled from the encoded mp4s, loudness measured on the mp4)
Scores 8 / 8 / 8 / 8 / 9 / 8
16:9 and 1:1 re-checked at the hook, typing, run and end card; keystroke sounds compared with the frames where the text changes (within one frame at 30 fps); -16.2 LUFS integrated, true peak -1.2 dBTP; both files 11.8 MB, faststart, yuv420p High, AAC 128k.
Left to improve: the app's own UI text is small on a phone (captions carry the meaning, nothing is upscaled); the footfalls are placed on a measured 0.4 s stride, not read from the skeleton; the middle (dialog 6 s) is the calmest stretch.
