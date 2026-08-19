# -*- coding: utf-8 -*-
import glob
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
for f in glob.glob("games/*/flow.json"):
    game = os.path.basename(os.path.dirname(f))
    flow = json.load(open(f, encoding="utf-8"))

    def walk(node):
        if isinstance(node, dict):
            if node.get("specifies") == "<ontology::push_track>":
                desc = node.get("description", {})
                dzh = desc.get("zh", "") if isinstance(desc, dict) else ""
                print(game, "| step=", repr(node.get("step")), "|", dzh[:50])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(flow)
