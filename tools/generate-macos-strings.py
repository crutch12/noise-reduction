"""Exports translations from app/Localization.cs and app/i18n/*.cs for the macOS app"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STR = r'"((?:[^"\\]|\\.)*)"'
PAIR = re.compile(r'\{\s*' + STR + r',\s*' + STR + r'\s*\}')


def unescape(value):
    return re.sub(r'\\(.)', lambda match: {'n': '\n', 't': '\t'}.get(match.group(1), match.group(1)), value)


def pairs(text):
    return {unescape(key): unescape(value) for key, value in PAIR.findall(text)}


def array(text, name):
    match = re.search(name + r'\s*=\s*\{(.*?)\}', text, re.S)
    return [unescape(value) for value in re.findall(STR, match.group(1))]


def main(output):
    source = (ROOT / 'app/Localization.cs').read_text(encoding='utf-8')
    start = source.index('English = new Dictionary')
    texts = {'en': pairs(source[start:source.index('};', start)]), 'ru': {}}
    for path in sorted((ROOT / 'app/i18n').glob('*.cs')):
        if path.stem != 'Translations':
            texts[path.stem] = pairs(path.read_text(encoding='utf-8'))
    ids, names = array(source, 'Ids'), array(source, 'Names')
    languages = [{'id': id, 'name': name} for id, name in zip(ids, names)]
    Path(output).write_text(json.dumps({'languages': languages, 'texts': texts}, ensure_ascii=False), encoding='utf-8')


if __name__ == '__main__':
    main(sys.argv[1])
