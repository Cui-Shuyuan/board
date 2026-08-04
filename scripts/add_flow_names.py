import json

with open('games/civolution/flow.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

# Manual name mapping for all flow nodes
names = {
    # Top-level flow concepts
    'game': ('游戏', 'Game'),
    'setup': ('设置', 'Setup'),
    'era_loop': ('时代循环', 'Era Loop'),
    'final_scoring': ('终局计分', 'Final Scoring'),

    # Procedure nodes
    'action_turn_cycle': ('行动轮循环', 'Action Turn Cycle'),
    'player_action_turn': ('玩家行动回合', 'Player Action Turn'),
    'event_weather': ('天气结算', 'Weather Resolution'),
    'event_card_resolution': ('事件牌结算', 'Event Card Resolution'),
    'event_era_scoring': ('时代计分', 'Era Scoring'),
    'event_new_starting_player': ('新起始玩家', 'New Starting Player'),
    'final_scoring_categories': ('终局计分项', 'Final Scoring Categories'),
    'final_scoring_stage_partition': ('阶段分区计分', 'Stage Partition Scoring'),
    'final_scoring_point_bonus_cards': ('分数加成牌计分', 'Point Bonus Card Scoring'),
    'determine_winner': ('判定胜者', 'Determine Winner'),

    # Pipeline step nodes (internal)
    'place': ('放置', 'Place'),
    'gain': ('获得', 'Gain'),
    'roll': ('掷骰', 'Roll'),
    'install': ('安装', 'Install'),
    'reward': ('奖励', 'Reward'),
    'pay_money': ('支付钱币', 'Pay Money'),
    'pay_cost': ('支付费用', 'Pay Cost'),
    'place_farm': ('放置农场', 'Place Farm'),
    'place_boat': ('放置船', 'Place Boat'),
    'place_statue': ('放置雕像', 'Place Statue'),
    'gain_idea': ('获得创意标记', 'Gain Idea Marker'),
    'gain_material': ('获得材料', 'Gain Material'),
    'gain_money': ('获得钱币', 'Gain Money'),
    'gain_income_chip_reward': ('获得收入芯片奖励', 'Gain Income Chip Reward'),
    'gain_points': ('获得分数', 'Gain Points'),
    'board_tribe': ('部落登船', 'Board Tribe'),
    'choose_module': ('选择模组', 'Choose Module'),
    'choose_territory': ('选择领地', 'Choose Territory'),
    'choose_die_result': ('选择骰子结果', 'Choose Die Result'),
    'choose_building_ground': ('选择建造点', 'Choose Building Ground'),
    'choose_settlement_slot': ('选择聚落格', 'Choose Settlement Slot'),
    'determine_position': ('确定安装位置', 'Determine Position'),
    'determine_cost_range': ('确定费用范围', 'Determine Cost Range'),
    'take_from_display': ('从展示区拿取', 'Take From Display'),
    'refill_display': ('补满展示区', 'Refill Display'),
    'trigger_effect': ('触发效果', 'Trigger Effect'),
    'return_material_sm': ('返还材料换钱', 'Return Material For Money'),
    'return_material_sp': ('返还材料换分', 'Return Material For Points'),
    'sale': ('出售', 'Sale'),
    'sale_for_money': ('换钱', 'Sale For Money'),
    'sale_for_points': ('换分', 'Sale For Points'),
    'purchase': ('购买', 'Purchase'),
    'upgrade_l1_to_l2': ('升级L1到L2', 'Upgrade L1 to L2'),
    'upgrade_l2_to_l3': ('升级L2到L3', 'Upgrade L2 to L3'),
    'roll_fate_dice': ('投命运骰', 'Roll Fate Dice'),

    # Ontology/internal concepts that appear in flow
    'transfer': ('转移', 'Transfer'),
    'condition': ('条件', 'Condition'),
    '<ontology::transfer>': ('转移', 'Transfer'),
    '<ontology::instant_cost>': ('即时费用', 'Instant Cost'),
    '<favor_test>': ('恩惠检定', 'Favor Test'),
    '<push_track>': ('推进轨道', 'Push Track'),
    '<gain_income_chip>': ('获得收入芯片', 'Gain Income Chip'),
    '<activate_income_chip>': ('激活收入芯片', 'Activate Income Chip'),
    '<install_income_chip>': ('安装收入芯片', 'Install Income Chip'),
    '<lose_food>': ('失去食物', 'Lose Food'),
    '<remove_tribe>': ('移除部落', 'Remove Tribe'),

    # Setup event nodes
    'assemble_public_board': ('组装公共版图', 'Assemble Public Board'),
    'place_sites': ('放置地点', 'Place Sites'),
    'place_progress_and_sequence_boards': ('放置进程流程版图', 'Place Progress & Sequence Boards'),
    'prepare_phase_indicator': ('准备阶段标记', 'Prepare Phase Indicator'),
    'prepare_weather_indicator': ('准备天气标记', 'Prepare Weather Indicator'),
    'prepare_event_cards': ('准备事件牌', 'Prepare Event Cards'),
    'prepare_research_decks': ('准备研究牌堆', 'Prepare Research Decks'),
    'prepare_starting_decks': ('准备初始牌堆', 'Prepare Starting Decks'),
    'prepare_era_scoring_tiles': ('准备时代计分板块', 'Prepare Era Scoring Tiles'),
    'prepare_final_scoring_tiles': ('准备终局计分板块', 'Prepare Final Scoring Tiles'),
    'prepare_hundred_point_tokens': ('准备百分指示物', 'Prepare 100-Point Tokens'),
    'prepare_hunting_tokens': ('准备狩猎指示物', 'Prepare Hunting Tokens'),
    'prepare_dice_pool': ('准备骰子池', 'Prepare Dice Pool'),
    'prepare_goal_chip_display': ('准备目标芯片展示', 'Prepare Goal Chip Display'),
    'prepare_income_chip_display': ('准备收入芯片展示', 'Prepare Income Chip Display'),
    'prepare_attribute_chip_display': ('准备属性芯片展示', 'Prepare Attribute Chip Display'),
    'place_material_tiles': ('放置材料板块', 'Place Material Tiles'),
    'player_setup_modules': ('玩家设置模组', 'Player Setup Modules'),
    'player_setup_dice': ('玩家设置骰子', 'Player Setup Dice'),
    'player_setup_fate_die': ('玩家设置命运骰', 'Player Setup Fate Die'),
    'player_setup_stage_tiles': ('玩家设置层级板块', 'Player Setup Stage Tiles'),
    'player_setup_buildings': ('玩家设置建筑', 'Player Setup Buildings'),
    'player_setup_score_and_track_tokens': ('玩家设置分数轨标记', 'Player Setup Score & Track Tokens'),
    'player_setup_favor_marker': ('玩家设置恩惠标记', 'Player Setup Favor Marker'),
    'player_setup_technology_marker': ('玩家设置科技标记', 'Player Setup Technology Marker'),
    'player_setup_prestige_marker': ('玩家设置声望标记', 'Player Setup Prestige Marker'),
    'player_setup_knowledge_marker': ('玩家设置知识标记', 'Player Setup Knowledge Marker'),
    'player_setup_construction_marker': ('玩家设置建设标记', 'Player Setup Construction Marker'),
    'player_setup_culture_marker': ('玩家设置文化标记', 'Player Setup Culture Marker'),
    'player_place_initial_tribes': ('玩家放置初始部落', 'Player Place Initial Tribes'),
    'player_place_initial_markers': ('玩家放置初始标记', 'Player Place Initial Markers'),
    'choose_starting_player': ('选择起始玩家', 'Choose Starting Player'),
    'draft_starting_marker_cards': ('轮抽初始标记牌', 'Draft Starting Marker Cards'),
    'draft_starting_chip_cards': ('轮抽初始芯片牌', 'Draft Starting Chip Cards'),
    'draft_starting_research_cards': ('轮抽初始研究牌', 'Draft Starting Research Cards'),
    'evaluate_condition': ('评估条件', 'Evaluate Condition'),
    'push_track': ('推进轨道', 'Push Track'),
    'push_any_progress_track': ('推进任意进程轨', 'Push Any Progress Track'),
}

def get_node_id(node):
    nid = node.get('id', '')
    if nid:
        return nid
    # For nodes without id, use concept key (e.g. "<ontology::transfer>": {...})
    for k in node:
        if k.startswith('<') and k.endswith('>') and isinstance(node[k], dict):
            return k
    return ''

def walk(node):
    nid = get_node_id(node)
    if nid and nid in names and 'name' not in node:
        node['name'] = {'zh': names[nid][0], 'en': names[nid][1]}

    for key in ('children', 'events', 'options'):
        for child in node.get(key, []):
            if isinstance(child, dict):
                walk(child)
            # Handle dicts nested under concept keys in options
            if isinstance(child, dict):
                for ck, cv in child.items():
                    if ck.startswith('<') and isinstance(cv, dict):
                        walk(cv)

    for container_key in ('<ontology::content>', '<ontology::cost>', '<ontology::condition>',
                          '<ontology::instant_content>', '<ontology::instant_cost>',
                          '<ontology::continuous_effect>', '<ontology::effect>'):
        container = node.get(container_key)
        if isinstance(container, dict):
            walk(container)

# Walk both procedures and triggers
for proc in data.get('procedures', []):
    walk(proc)
for trigger in data.get('triggers', []):
    walk(trigger)

# Also walk the top-level pipeline if present
pipeline = data.get('pipeline', {})
for opt in pipeline.get('options', []):
    if isinstance(opt, dict):
        walk(opt)

with open('games/civolution/flow.json', 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
    f.write('\n')

print('Done')
