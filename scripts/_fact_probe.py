# -*- coding: utf-8 -*-
"""临时探针：从 API 日志抽取 fact JSON 样本及其归属请求。"""
import io
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

raw = open(
    r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\b8oahkbbp.output",
    encoding="utf-8", errors="replace",
).read()

headers = [(m.start(), m.group(1), m.group(2)[:44])
           for m in re.finditer(r"\[Chat\] game: (\S+?), question: (.*?), count: \d+", raw)]


def game_of(pos):
    g = q = None
    for s, g_, q_ in headers:
        if s > pos:
            break
        g, q = g_, q_
    return g, q


pat = re.compile(r'"kind"\s*:\s*"(quantity_numeric|score_[a-z_]+)"')
for m in pat.finditer(raw):
    start = raw.rfind("{", 0, max(0, m.start() - 200))
    seg = raw[start:m.end() + 400].replace("\n", " ")
    g, q = game_of(m.start())
    print("== %s @ %s | %s" % (m.group(1), g, q))
    print(seg[:400])
    print()
