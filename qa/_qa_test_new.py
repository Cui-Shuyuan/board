# -*- coding: utf-8 -*-
"""模拟客人提问（第二轮 50 题）：基于规则书概念设计的深水区问题
覆盖：模组细节 / 计分类别 / 版图区域 / 冷门规则 / 跨游戏混淆 / 不相关拒绝
"""
import json
import time
import urllib.request

API = 'http://localhost:5000/api/chat'

QUESTIONS = [
    # ---- 模组细节 ----
    "睡眠模组怎么激活？能干嘛？",
    "探索模组翻开的地点什么时候结算？",
    "洞察模组是干什么的？",
    "突变模组能干嘛？",
    "发明模组是什么效果？",
    "成就模组怎么得分？",
    "活动模组激活后可以选什么活动？",
    "研究模组能让我拿研究牌吗？",
    "生产模组一次能生产几个材料？",
    "贸易模组能买材料和卖材料吗？",
    "给养模组怎么喂部落？",
    "运输模组能运几个材料？",
    "计划模组是干嘛的？",
    # ---- 计分类别 ----
    "九个计分类别都有什么？",
    "繁荣类别怎么算分？",
    "人口类别是数什么的？",
    "扩张类别是数什么的？",
    "进化类别是数什么的？",
    "进程轨推到顶再推会怎样？",
    "进程轨上的奖励线能拿什么奖励？",
    "时代计分什么时候发生？",
    # ---- 版图/区域 ----
    "火营地有什么特别？",
    "建造点能建什么？",
    "农场怎么建？建了干嘛？",
    "船最多装几个部落？",
    "水区域上能建农场吗？",
    "山脉区域有什么特殊规则吗？",
    "狩猎在哪查表？",
    "什么叫幸运收获？",
    "焦点标记有什么用？",
    "计划标记能替代骰子吗？",
    "创意标记怎么获得？",
    # ---- 冷门规则 ----
    "我的标记最多有几个？",
    "20 个部落全在场上还能繁育吗？",
    "能拆掉自己建好的聚落吗？",
    "建筑牌安装时可以忽略费用格吗？",
    "研究牌怎么装到第四第五层？",
    "安装研究牌时多付费用有什么好处？",
    "收入芯片一共几个可以选？",
    "目标芯片装完一叠会发生什么？",
    "属性芯片装到哪？",
    "事件牌右上角的天气趋势怎么读？",
    "天气轨白线外会怎样？",
    "新时代开始阶段标记会怎样？",
    "起始玩家什么时候换？怎么换？",
    # ---- 跨游戏混淆（civolution 里问 splendor）----
    "璀璨宝石里我拿宝石有上限吗？",
    "宝石商店里的卡什么时候补？",
    # ---- 不相关/拒绝 ----
    "今天天气怎么样？",
    "你能帮我写一封求职信吗？",
    "1+1 等于几？",
    "你叫什么名字？",
]

def ask(game_id, q):
    body = json.dumps({
        'game_id': game_id,
        'messages': [{'role': 'user', 'content': q}]
    }, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(API, data=body, headers={'Content-Type': 'application/json'})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.loads(resp.read().decode('utf-8'))
        dt = time.time() - t0
        return data.get('reply', '(no reply)'), dt
    except Exception as e:
        return f'(ERROR: {e})', time.time() - t0

def main():
    results = []
    for i, q in enumerate(QUESTIONS, 1):
        reply, dt = ask('civolution', q)
        results.append((i, q, reply, dt))
        print(f'[{i:02d}] {dt:6.1f}s  Q: {q}')
        print(f'      A: {reply}')
        print()
        time.sleep(0.5)

    total = sum(r[3] for r in results)
    avg = total / len(results)
    print('=' * 60)
    print(f'共 {len(results)} 题，总耗时 {total:.1f}s，平均 {avg:.1f}s')
    slow = [r for r in results if r[3] > 15]
    print(f'慢于 15s 的 {len(slow)} 题：{[r[0] for r in slow]}')

    with open(r'D:\workspace\board\scripts\_qa_results_new.json', 'w', encoding='utf-8') as f:
        json.dump([{'n': n, 'q': q, 'reply': r, 'secs': round(s, 1)} for n, q, r, s in results],
                  f, ensure_ascii=False, indent=2)

if __name__ == '__main__':
    main()
