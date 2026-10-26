"""Checks app/i18n/<id>.cs files against the Russian source keys in app/Localization.cs"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STR = r'"((?:[^"\\]|\\.)*)"'
PAIR = re.compile(r'\{\s*' + STR + r',\s*' + STR + r'\s*\}')
TOKENS = ('Noise Reduction', 'RNNoise', 'DeepFilterNet', 'Ctrl+Alt+N', 'Windows', 'USB')


def english():
    text = (ROOT / 'app/Localization.cs').read_text(encoding='utf-8')
    start = text.index('English = new Dictionary')
    return dict(PAIR.findall(text[start:text.index('};', start)]))


def check(path, source):
    text = path.read_text(encoding='utf-8')
    field = path.stem[0].upper() + path.stem[1:]
    problems = []
    if f'Dictionary<string, string> {field} = ' not in text:
        problems.append(f'field {field} is missing')
    pairs = PAIR.findall(text)
    seen = {}
    for key, value in pairs:
        if key in seen:
            problems.append(f'duplicate key: {key}')
        seen[key] = value
    for key in source:
        if key not in seen:
            problems.append(f'missing key: {key}')
        elif not seen[key].strip():
            problems.append(f'empty translation: {key}')
    for key in seen:
        if key not in source:
            problems.append(f'unknown key: {key}')
    for key, value in seen.items():
        if key not in source:
            continue
        if ('&' in key) != ('&' in value):
            problems.append(f'access key "&" mismatch: {key}')
        for token in TOKENS:
            if token in source[key] and token not in value and token != 'Windows':
                problems.append(f'"{token}" must stay untranslated: {key}')
        if re.search('[а-яА-ЯёЁ]', value) and path.stem not in ('uk',):
            problems.append(f'Cyrillic left in translation: {key}')
    return problems


def main():
    source = english()
    names = sys.argv[1:] or [p.stem for p in sorted((ROOT / 'app/i18n').glob('*.cs')) if p.stem != 'Translations']
    failed = False
    for name in names:
        problems = check(ROOT / 'app/i18n' / f'{name}.cs', source)
        print(f'{name}: {"OK" if not problems else str(len(problems)) + " problems"}')
        for problem in problems[:40]:
            print('  ' + problem)
        failed = failed or bool(problems)
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
