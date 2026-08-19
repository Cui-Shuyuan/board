# -*- coding: utf-8 -*-
"""
规则文件规范化：去 BOM、去制表符、统一 LF、解码 \\uXXXX 转义、去行尾空白。
统一为：UTF-8 无 BOM、LF、2 空格缩进、ensure_ascii=False、文件末尾单个换行。

用法: python scripts/normalize_json.py [--check]
  --check 只报告不写入
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

TARGETS = sorted(set(
    list((ROOT / 'ontology').glob('*.json'))
    + list((ROOT / 'games').glob('*/*.json'))
    + [ROOT / 'backend' / 'BoardAI.Api' / 'appsettings.json']
))


def analyze(raw: bytes) -> dict:
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig' if bom else 'utf-8')
    return {
        'bom': bom,
        'tabs': '\t' in text,
        'crlf': '\r\n' in text,
        'uescape': '\\u' in text,
        'trailws': any(line.rstrip() != line for line in text.split('\n') if line),
        'no_final_nl': not text.endswith('\n'),
    }


def normalize(path: Path, check: bool):
    raw = path.read_bytes()
    issues = analyze(raw)
    if not any(issues.values()):
        return None

    data = json.loads(raw.decode('utf-8-sig'))
    if check:
        return issues

    text = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
    # 行尾空白（json.dumps 本身不会产生，保险）
    text = '\n'.join(line.rstrip() for line in text.split('\n')) + '\n'
    path.write_text(text, encoding='utf-8', newline='\n')
    return issues


def main():
    check = '--check' in sys.argv
    changed, clean = [], 0
    for p in TARGETS:
        issues = normalize(p, check)
        if issues is None:
            clean += 1
            continue
        detail = ' '.join(k for k, v in issues.items() if v)
        print('%-55s %s%s' % (str(p).replace(str(ROOT) + '\\', ''), '(check) ' if check else '已规范化: ', detail))
        changed.append(p)
    print()
    print(f'共 {len(TARGETS)} 个文件: {clean} 个干净, {len(changed)} 个{'待规范' if check else '已规范'}')


if __name__ == '__main__':
    main()
