# -*- coding: utf-8 -*-
"""回归测试：重测之前出错的 4 题 + 直接验证 get_concept 返回的注解效果"""
import json
import time
import urllib.request

API = 'http://localhost:5000/api/chat'
RULES = 'http://localhost:5000/api/rules'

QUESTIONS = [
    "睡眠模组是什么？",
    "神秘橡树怎么计分？",
    "怎么激活模组？",
    "恩惠检定是什么？",
]

def ask(game_id, q):
    body = json.dumps({
        'game_id': game_id,
        'messages': [{'role': 'user', 'content': q}]
    }, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(API, data=body, headers={'Content-Type': 'application/json'})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = json.loads(resp.read().decode('utf-8'))
    return data.get('reply', ''), time.time() - t0

def get_concept_raw(game, cid):
    url = f'{RULES}/games/{game}/concepts/{cid}'
    with urllib.request.urlopen(url, timeout=30) as resp:
        return resp.read().decode('utf-8')

print('=== 直接验证 get_concept 注解效果 ===')
for cid in ('idea_marker', 'focus_marker', 'hill_territory', 'module_sleep', 'tribe_death'):
    raw = get_concept_raw('civolution', cid)
    has_annotation = '(创意标记)' in raw or '(焦点标记)' in raw or '(丘陵)' in raw or '(睡眠)' in raw or '(部落死亡)' in raw
    # 找第一条带注解的引用样例
    import re
    sample = re.findall(r'<[a-z_]+>\([^)]+\)', raw)
    print(f'  {cid}: annotation={"YES" if has_annotation else "NO"}  sample={sample[:3]}')

print()
print('=== 重测 4 题 ===')
for i, q in enumerate(QUESTIONS, 1):
    reply, dt = ask('civolution', q)
    print(f'[{i}] {dt:6.1f}s  Q: {q}')
    print(f'     A: {reply}')
    print()
