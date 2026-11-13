#!/usr/bin/env python3
"""Fails (exit 1) when a user-visible English literal has no msgid in translations/ar.po.

Checked: Tr("..."), TranslationServer.Translate("..."), Notify("..."), W.Label("...") and command labels
(`Label => "..."` / `Label = "..."` in src/**/*.cs). Usage: python3 app/godot/tools/check_translations.py
"""
import re, sys, pathlib

HERE = pathlib.Path(__file__).resolve().parent.parent
ROOT = HERE.parent.parent
STR = r'"((?:[^"\\\n]|\\.)*)"'
PATTERNS = [
    re.compile(r'\bTr\(\s*' + STR),
    re.compile(r'TranslationServer\.Translate\(\s*' + STR),
    re.compile(r'\bNotify(?:Action)?(?:Format)?\(\s*' + STR),
    re.compile(r'\bNotifyActionFormat\(\s*' + STR + r',\s*\w+(?:\.\w+)*,\s*[\w.]+,\s*' + STR),
    re.compile(r'\bNotifyAction\(\s*' + STR + r',\s*[\w.]+,\s*' + STR),
    re.compile(r'(?:=>|\?|:)\s*' + STR + r',?\s*//\s*tr\b'),
    re.compile(r'\bW\.Label\(\s*' + STR),
]
LABEL = re.compile(r'\bLabel\s*(?:=>|=)\s*' + STR)
# Messages that are deliberately not translated (identifiers, format-only strings).
IGNORE = re.compile(r'^[^A-Za-z]*$')

def unescape(s):
    return s.replace('\\"', '"').replace('\\\\', '\\').replace('\\n', '\n').replace('\\t', '\t')

def load_po():
    txt = (HERE / 'translations' / 'ar.po').read_text(encoding='utf-8')
    ids = set()
    for m in re.finditer(r'^msgid ((?:"(?:[^"\\]|\\.)*"\n?)+)', txt, re.M):
        ids.add(unescape(''.join(re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1)))))
    return ids

def main():
    ids = load_po()
    missing = {}
    def check(text, where):
        text = unescape(text)
        # bilingual literals (already Arabic) are shown as they are
        if text and not text.endswith(' ') and not IGNORE.match(text) and not re.search('[\u0600-\u06FF]', text) and text not in ids:
            missing.setdefault(text, where)
    for f in (HERE / "scripts").rglob("*.cs"):
        if f.name.startswith("SelfTest"): continue
        src = f.read_text(encoding='utf-8')
        for p in PATTERNS:
            for m in p.finditer(src):
                check(m.group(1), f.name)
    for f in (ROOT / 'src').rglob('*.cs'):
        if '/obj/' in str(f) or '/bin/' in str(f): continue
        src = f.read_text(encoding='utf-8')
        for m in LABEL.finditer(src):
            check(m.group(1), f.name)
    if missing:
        print(f'{len(missing)} strings lack an ar.po msgid:')
        for t, w in sorted(missing.items()): print(f'  [{w}] {t!r}')
        return 1
    print('translations OK')
    return 0

if __name__ == '__main__':
    sys.exit(main())
