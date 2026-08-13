# Civolution FAQ 标注（50 道多人题 → 查询关系分类）

来源：faq.md（Grok 抓取 BGG 等社区真实玩家提问）。单人模式（V.I.C.I.）暂不标注。
每题标注：关系类（查询计划中的 relation）+ 实体入参。答案以我们的形式化为准（faq.md 中已确认错误的答案不采信）。

## 频率统计

| 关系类 | 数量 | 占比 |
|---|---|---|
| explain（X 是什么/怎么结算/效果闭包） | 26 | 52% |
| condition（能不能/什么前提） | 6 | 12% |
| quantity（数量/参数） | 5 | 10% |
| ordering（顺序/何时结束） | 4 | 8% |
| boundary（不会发生/满了/跨域持续） | 4 | 8% |
| timing（效果何时可用） | 2 | 4% |
| substitution（替代语义） | 2 | 4% |
| meta（规则书本体，off-domain） | 1 | 2% |

## 逐题标注

### explain（26 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 10 | upgrade_main_module | 如何从 II 级升级到 III 级 |
| 12 | install_research_card | 如何确定列与阶段 |
| 14 | refill_if_empty（研究牌正面堆补牌） | 安装导致正面堆空了怎么办 |
| 16 | adjust_die_value | Idea 如何修改骰子，1 和 6 循环吗 |
| 19 | replenish_marker_supply | 标记供应空了怎么办 |
| 20 | phase_5_site + gorge/glacier/mystic_oak | 地点阶段三地点如何结算 |
| 23 | feed_tribes | 必须逐个喂吗，能不喂荒野省食物吗 |
| 24 | feed_tribes（cost 部分）+ farm | 船上/营地/荒野食物成本，农场如何帮助 |
| 25 | deus_ex_machina | 这是什么 |
| 26 | score_strong_tribes | 喂养后每个强部落得 1 分吗 |
| 27 | adjust_weather_indicator + weather_gauge | 天气标记如何移动，极端时发生什么 |
| 29 | event_era_scoring + event_new_starting_player | 时代结束计分如何，谁成为先手 |
| 30 | final_scoring | 终局计分项目 |
| 31 | score_lap | 分数轨跑完一圈 |
| 34 | displace_occupant + weaken | 推入荒野会削弱吗（FAQ 答案错，我们正确） |
| 35 | lucky_find + adjust_die_value | 幸运发现与 Idea 互动 |
| 36 | score_prosperity | 存储区钻石如何计分 |
| 37 | develop_territory / flip_material_tile | 探索揭示资源要立刻放标记吗 |
| 38 | build_farm/build_boat/build_statue/build_settlement | 建造四件套要求与收益 |
| 39 | install_attribute_chip | 如何安装属性芯片，特征要求 |
| 40 | install_goal_chip + gain_goal_chip | 目标芯片如何运作 |
| 41 | install_income_chip vs gain_income_chip | 安装收入芯片立即执行吗（区分获得/安装） |
| 42 | perform_activity | Activity 模组做什么 |
| 43 | favor_test | Favor 测试如何进行 |
| 47 | score_statues | 雕像如何计分 |
| 50 | install_research_card（EXECUTE_ALL 语义） | 付更高阶段成本仍能拿即时奖励吗 |

### condition（6 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 6 | reset | 还有 ≥4 骰能提前 Reset 吗 |
| 11 | upgrade_main_module（target） | 可以升级任意主模组吗 |
| 13 | install_research_card | 能支付更高阶段成本获得更多分吗 |
| 33 | migrate | 弱化部落能迁移吗 |
| 46 | event_card（平局语义） | 多数事件平局可以吗 |
| 49 | deus_ex_machina | 死亡总能使用吗，有无限制 |

### quantity（5 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 1 | draft_starting_marker_cards | 设置时从几张起始标记卡中选择 |
| 2 | draft_starting_research_cards | 每种研究牌发几张供选择 |
| 15 | research_card（mutation 类型性质） | 突变牌即时奖励给进程轨步数吗 |
| 44 | research_card（mutation 类型性质） | 突变牌成本空间只有特征要求吗 |
| 45 | research_card（building 类型性质） | 建筑牌有定居点替代要求吗 |

### ordering（4 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 4 | phase_4_action + action_phase_end | Reset 后行动阶段何时结束 |
| 5 | phase_4_action + action_phase_end | Reset 与回合结束怎么处理 |
| 8 | reset + action_phase_end | 每时代保证多少次 Reset（派生，不入库） |
| 28 | event_weather vs event_card_resolution | 事件卡相对天气何时结算 |

### boundary（4 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 7 | era_loop（跨时代持续） | 时代之间有免费 Reset 吗，骰子留在哪 |
| 21 | phase_5_site（结算范围） | 其他地点在地点阶段也生效吗 |
| 22 | feed_tribes（闭包不含 strengthen） | 喂养弱化部落会重新变强吗 |
| 32 | favor_of_ager_track（溢出缺省） | Favor 满后再前进怎么办 |

### timing（2 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 9 | upgrade_main_module（升级时点） | 激活模组时升级，能立刻用新等级吗 |
| 48 | research_ability（生效范围） | 喂养阶段可用降成本牌能力吗 |

### substitution（2 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 17 | focus_marker | 能替代任意骰值吗 |
| 18 | planning_marker | 能替代激活骰吗 |

### meta（1 题）
| # | 实体 | 问题要点 |
|---|---|---|
| 3 | ——（规则书本体） | 术语表是规则书的一部分吗（off-domain，走兜底） |

## 结论

- **explain 占 52%**——P0 只实现 explain 一个 relation 就能覆盖一半真实问题
- 第二梯队（condition/quantity/ordering/boundary 共 19 题 38%）是后续函数实现顺序
- meta 类永远走兜底
