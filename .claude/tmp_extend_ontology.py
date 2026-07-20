import json
from pathlib import Path

path = Path("D:/workspace/board/ontology/ontology.json")
with open(path, 'r', encoding='utf-8') as f:
    data = json.load(f)

concepts = data['concepts']
play_idx = next(i for i, c in enumerate(concepts) if c['id'] == 'play')

new_concepts = [
    {
        "id": "dice",
        "name": {"zh": "骰子", "en": "Dice"},
        "level": 1,
        "parent": "<object>",
        "abstract": False,
        "definition": {
            "zh": "用于产生随机数值的游戏组件。<dice> 继承自 <object>。每颗骰子有若干面，每面印有一个数值或符号；通过 <die_roll> 事件掷出后，根据朝上的一面确定结果。骰子本身没有 <effect>，不可被 <play>，只是随机数生成器。",
            "en": "A game component used to generate random values. <dice> extends <object>. Each die has several faces, each printed with a value or symbol; after being rolled via a <die_roll> event, the result is determined by the face that lands upward. Dice themselves have no <effect> and cannot be <play>ed — they are random number generators."
        },
        "sides": {
            "type": "integer",
            "description": {
                "zh": "骰子的面数。如六面骰为 6。",
                "en": "Number of faces on the die. E.g., 6 for a six-sided die."
            }
        },
        "constraints": {
            "required": [],
            "optional": [
                {
                    "id": "sides",
                    "description": {
                        "zh": "骰子面数，用于规则说明和随机结果范围。",
                        "en": "Number of faces, used for rule explanation and random result range."
                    }
                }
            ]
        }
    },
    {
        "id": "terrain",
        "name": {"zh": "地形", "en": "Terrain"},
        "level": 1,
        "parent": "<object>",
        "abstract": False,
        "definition": {
            "zh": "描述游戏版图上某片区域的自然或地貌类型。<terrain> 继承自 <object>。它是分类概念，本身不携带 <effect>，但会影响相邻、资源产出、移动等规则。典型例子包括森林、草原、山脉、水域等。",
            "en": "A classification describing the natural or landscape type of an area on the game board. <terrain> extends <object>. It is a categorical concept that carries no <effect> itself but influences adjacency, resource production, movement, and other rules. Typical examples include Forest, Grassland, Mountains, Water, etc."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    },
    {
        "id": "region",
        "name": {"zh": "区域", "en": "Region"},
        "level": 2,
        "parent": "<zone>",
        "abstract": False,
        "definition": {
            "zh": "由连续同类型 <terrain> 组成的一片版图区域。<region> 继承自 <zone>，是一种逻辑容器。同一 <terrain> 类型但不相连的部分算作不同 <region>；相邻 <region> 之间通过边界接壤。<region> 可以包含 <campsite>、<piece>、<token> 等，并可能关联一个 <terrain> 类型和一个 <material_tile>。",
            "en": "A board area consisting of contiguous spaces of the same <terrain> type. <region> extends <zone> as a logical container. Areas of the same <terrain> type that are not connected count as separate <region>s; adjacent <region>s share a border. A <region> may contain <campsite>s, <piece>s, <token>s, and may be associated with a <terrain> type and a material tile."
        },
        "<terrain>": {
            "type": "<terrain>",
            "description": {
                "zh": "此区域的地形类型。",
                "en": "The terrain type of this region."
            }
        },
        "constraints": {
            "required": [
                {
                    "id": "<terrain>",
                    "description": {
                        "zh": "区域所属的地形类型。",
                        "en": "The terrain type this region belongs to."
                    }
                }
            ],
            "optional": []
        }
    },
    {
        "id": "campsite",
        "name": {"zh": "营地", "en": "Campsite"},
        "level": 2,
        "parent": "<object>",
        "abstract": False,
        "definition": {
            "zh": "位于 <region> 内的特定位置，用于容纳 <tribe> 等 <piece>。<campsite> 继承自 <object>。一个 <region> 内可能有多个 <campsite>，某些营地可能有特殊效果（如火边营地提供分数）。<campsite> 本身不改变游戏状态，只是位置标记。",
            "en": "A specific location within a <region> that can hold <piece>s such as <tribe>s. <campsite> extends <object>. A <region> may contain multiple <campsite>s, and some campsites may have special effects (e.g., fire encampments grant points). A <campsite> itself does not change game state; it is merely a position marker."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    },
    {
        "id": "site",
        "name": {"zh": "地点板块", "en": "Site"},
        "level": 2,
        "parent": "<tile>",
        "abstract": False,
        "definition": {
            "zh": "放置在公共版图上、影响相邻 <region> 的特殊 <tile>。<site> 继承自 <tile>。通常初始背面朝上，通过特定 <action>（如探索）翻开后生效。每个 <site> 有独特的被动效果，在特定阶段或触发条件下结算。",
            "en": "A special <tile> placed on the public board that affects adjacent <region>s. <site> extends <tile>. Usually placed face down at the start and revealed by a specific <action> (e.g., exploration) to take effect. Each <site> has a unique passive effect that resolves in specific phases or under trigger conditions."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    },
    {
        "id": "alternative_cost",
        "name": {"zh": "替代费用", "en": "Alternative Cost"},
        "level": 2,
        "parent": "<property>",
        "abstract": False,
        "definition": {
            "zh": "满足其中任意一个即可的费用形式。<alternative_cost> 继承自 <property>。用于表达「支付 A 或满足 B 即可」的规则——玩家只需完成所列条件之一，该费用即视为已支付。多个替代条件之间是 OR 关系。",
            "en": "A cost form where fulfilling any one of the listed conditions is sufficient. <alternative_cost> extends <property>. Used to express rules like 'pay A or satisfy B' — the player only needs to complete one of the listed conditions for the cost to be considered paid. Multiple alternative conditions are in an OR relationship."
        },
        "options": {
            "type": "<cost>[]",
            "description": {
                "zh": "可供选择的替代条件列表。玩家只需满足其中一项。",
                "en": "List of alternative conditions; the player only needs to satisfy one."
            }
        },
        "constraints": {
            "required": [
                {
                    "id": "options",
                    "description": {
                        "zh": "替代条件选项列表，玩家满足其中任意一个即可。",
                        "en": "List of alternative cost options; satisfying any one is enough."
                    }
                }
            ],
            "optional": []
        }
    },
    {
        "id": "choice",
        "name": {"zh": "选择", "en": "Choice"},
        "level": 1,
        "parent": "<property>",
        "abstract": False,
        "definition": {
            "zh": "描述玩家在执行某个 <action> 或 <effect> 时必须做出的多选一决策。<choice> 继承自 <property>。它本身不是费用或效果，而是对「玩家必须选择一项」这一规则事实的声明。典型例子：「二选一行动」「从三种资源中选择一种」。",
            "en": "Describes a multi-option decision a player must make when executing an <action> or <effect>. <choice> extends <property>. It is neither a cost nor an effect itself, but a declaration of the rule fact that 'the player must choose one option'. Typical examples: 'choose one of two actions', 'choose one of three resources'."
        },
        "options": {
            "type": "map<string, any>[]",
            "description": {
                "zh": "可选的选项列表。每个选项是一个规格对象，由具体游戏规则定义。",
                "en": "List of available options. Each option is a specification object defined by the game rules."
            }
        },
        "constraints": {
            "required": [
                {
                    "id": "options",
                    "description": {
                        "zh": "玩家可选择的选项列表。",
                        "en": "List of options available to the player."
                    }
                }
            ],
            "optional": []
        }
    },
    {
        "id": "passive_effect",
        "name": {"zh": "被动效果", "en": "Passive Effect"},
        "level": 2,
        "parent": "<property>",
        "abstract": False,
        "definition": {
            "zh": "持续生效、无需玩家主动触发的效果规格。<passive_effect> 继承自 <property>。与需要 <activation> 的 <effect> 不同，被动效果一旦满足其前提条件就会自动生效，常见于 <site>、已安装的 <card> 或 <attribute_chip>。其运行时不产生独立的 <event>，而是修改规则判定或提供额外奖励。",
            "en": "A continuously active effect specification that does not require the player to actively trigger it. <passive_effect> extends <property>. Unlike <effect>s that require <activation>, passive effects automatically apply once their prerequisites are met, commonly found on <site>s, installed <card>s, or <attribute_chip>s. At runtime they do not produce standalone <event>s but modify rule evaluations or grant additional bonuses."
        },
        "trigger": {
            "type": "<trigger> | null",
            "description": {
                "zh": "此被动效果的生效前提。null 表示始终生效。",
                "en": "The prerequisite for this passive effect to apply. null means always active."
            }
        },
        "constraints": {
            "required": [],
            "optional": [
                {
                    "id": "trigger",
                    "description": {
                        "zh": "被动效果的生效前提，null 表示无条件持续生效。",
                        "en": "Prerequisite for the passive effect; null means always active."
                    }
                }
            ]
        }
    },
    {
        "id": "die_roll",
        "name": {"zh": "掷骰", "en": "Die Roll"},
        "level": 2,
        "parent": "<event>",
        "abstract": False,
        "definition": {
            "zh": "投掷一颗或多颗 <dice> 以产生随机数值的 <event>。<die_roll> 继承自 <event>。结果由骰子朝上的一面决定，可用于判定、费用、资源获取等。玩家通常可以花费创意标记等资源修改骰子点数。",
            "en": "An <event> in which one or more <dice> are rolled to generate random values. <die_roll> extends <event>. The result is determined by the face(s) that land upward and can be used for checks, costs, resource gains, etc. Players may often spend resources such as idea markers to modify die results."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    },
    {
        "id": "favor_test",
        "name": {"zh": "恩惠检定", "en": "Favor Test"},
        "level": 2,
        "parent": "<event>",
        "abstract": False,
        "definition": {
            "zh": "投掷玩家持有的全部粉骰子，并根据 <favor_of_ager_track> 当前位置判定是否通过的 <event>。<favor_test> 继承自 <event>。只要有一颗骰子的点数落在轨道允许的范围内，即视为通过。常用于模组升级后的额外行动。",
            "en": "An <event> in which a player rolls all their fate dice and checks whether any die result falls within the range allowed by the current position on the Favor of Agera track. <favor_test> extends <event>. Passing requires at least one die to show an allowed value. Commonly used for bonus actions after upgrading modules."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    },
    {
        "id": "upgrade",
        "name": {"zh": "升级", "en": "Upgrade"},
        "level": 2,
        "parent": "<event>",
        "abstract": False,
        "definition": {
            "zh": "将一个可升级的 <piece> 从当前等级提升至下一等级的 <event>。<upgrade> 继承自 <event>。升级通常表现为翻面（从等级一到等级二）或移除上层板块以露出下层（从等级二到等级三）。升级后的 <piece> 通常拥有更强或额外的效果。",
            "en": "An <event> that advances an upgradable <piece> from its current level to the next level. <upgrade> extends <event>. An upgrade is usually represented by flipping the piece (level I to level II) or removing the upper tile to reveal the lower one (level II to level III). The upgraded <piece> typically has stronger or additional effects."
        },
        "constraints": {
            "required": [],
            "optional": []
        }
    }
]

for c in reversed(new_concepts):
    concepts.insert(play_idx, c)

with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)

print(f"Inserted {len(new_concepts)} new concepts before 'play'.")
