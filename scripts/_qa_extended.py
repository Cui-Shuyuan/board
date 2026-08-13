# -*- coding: utf-8 -*-
"""扩展 QA：原 52 题 + 口语化/STT 模拟题 + 规则书各领域补题。单工具（execute_plan）架构回归用。"""
import json
import time
import urllib.request

API = 'http://localhost:5000/api/chat'

QUESTIONS = [
    # ---- 基础概念 ----
    "这个游戏怎么赢？",
    "部落有哪几种状态？",
    "营地是什么？有什么用？",
    "大陆上有哪几种地形？",
    "什么叫领地？两个同色相邻的区域算同一个吗？",
    "我的人口标记是干嘛的？",
    # ---- 阶段流程 ----
    "游戏一共有几个阶段？按什么顺序进行？",
    "阶段一干什么？",
    "阶段二每个人都要拿目标芯片吗？",
    "额外收获是什么意思？我该怎么做？",
    "行动阶段我要做什么？",
    "地点阶段会发生什么？",
    "喂养阶段怎么喂？",
    "事件阶段干什么？",
    "收入阶段做什么？",
    # ---- 行动 ----
    "怎么激活模组？",
    "重置是什么？怎么操作？",
    "什么时候能重置？",
    "怎么狩猎？",
    "怎么迁徙？",
    "怎么生产原材料？",
    "原材料怎么变成能用的东西？",
    "怎么建造？能建什么？",
    "怎么运输材料？",
    "怎么获得研究牌？",
    "研究牌怎么安装？有什么限制？",
    # ---- 骰子与标记 ----
    "激活骰和命运骰有什么区别？",
    "创意标记是干什么的？",
    "计划标记怎么用？",
    "焦点标记有什么用？",
    "1和6算相连吗？",
    "骰子点数不够怎么办？",
    # ---- 喂养/死亡 ----
    "喂不饱部落会怎样？",
    "什么叫机械降神？什么时候能用？",
    "机械降神用了会怎样？",
    # ---- 地点 ----
    "狼谷是什么效果？",
    "冰川会怎样？",
    "神秘橡树怎么计分？",
    "地点怎么翻开？",
    # ---- 芯片/牌 ----
    "目标芯片怎么获得？有什么用？",
    "收入芯片怎么获得？什么时候激活？",
    "属性芯片是干嘛的？",
    "事件牌有什么作用？",
    # ---- 模组/升级 ----
    "模组可以升级吗？怎么升？",
    "睡眠模组是什么？",
    "模组激活要什么骰子？",
    # ---- 恩惠/计分 ----
    "阿格拉恩惠轨是干嘛的？",
    "恩惠检定是什么？",
    "游戏什么时候结束？",
    "终局怎么计分？",
    # ---- 跨游戏 ----
    "贵族怎么获得？",
    "什么时候游戏结束？",
    # ============ 扩展：口语化 / STT 模拟 ============
    # ---- 口语填充/口癖 ----
    "就是这个...白色的那个骰子是干嘛的？",
    "我想问一下，就这个，模组怎么升级？",
    "嗯，呃，那个阿格拉恩惠是啥玩意儿？",
    "我要是喂不饱部落了会怎样？",
    "呃...怎么建造来着？",
    "那个篝火营地，就是那个有火的营地，有啥用？",
    "诶，等一下，这个回合我能干几件事？",
    "那啥，命运骰子和激活骰子，就是白色和粉色的，有啥区别？",
    # ---- 条件/能不能 ----
    "我还有四个骰子，能重置吗？",
    "骰子区空了还能重置吗？",
    "我能不能在行动阶段同时激活两个模组？",
    "研究牌能装到已经有属性芯片的列里吗？",
    "恩惠轨满格了我还能拿恩惠吗？",
    # ---- 顺序/先后 ----
    "行动阶段之后是什么阶段？",
    "事件阶段里先天气还是先结算事件牌？",
    "收入阶段结束以后回到哪？",
    "喂养阶段和事件阶段哪个先？",
    # ---- 边界/规则书细节 ----
    "如果我分数轨走完一圈会怎样？",
    "仓库能装多少材料？",
    "创意标记最多能存几个？",
    "我最多能造几艘船？",
    "聚落最多能建几个？",
    "起始玩家是谁先行动？",
    "时代计分的时候平局怎么办？",
    "雕像怎么计分？",
    "船上的部落要喂吗？",
    "材料板块翻面是什么意思？",
    # ---- 闲聊/非规则 ----
    "你好，你能陪我聊聊天吗？",
    "今天天气怎么样？",
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

    with open(r'D:\workspace\board\scripts\_qa_results.json', 'w', encoding='utf-8') as f:
        json.dump([{'n': n, 'q': q, 'reply': r, 'secs': round(s, 1)} for n, q, r, s in results],
                  f, ensure_ascii=False, indent=2)

if __name__ == '__main__':
    main()
