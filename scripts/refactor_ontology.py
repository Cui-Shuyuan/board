import json

# Load ontology
with open('ontology/concepts.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

concepts = data['concepts']

# 1. Remove declaration concept
concepts = [c for c in concepts if c['id'] != 'declaration']

# 2. Find and update action, effect, activation, trigger, event
for c in concepts:
    cid = c['id']

    if cid == 'action':
        # Change extends to specifies
        c['specifies'] = '<trigger>'
        del c['extends']

        # New definition
        c['definition'] = {
            'zh': (
                '一种 <trigger>——由 <player> 的决策直接点燃，不依赖任何 <piece> 即可存在。'
                '<action> 与 <trigger> 共享完全相同的结构（<condition> → <cost> → <target> → <content>），'
                '区别不在本体层面，而在称呼习惯：规则书和玩家将出现在回合菜单中、由玩家做出、且不依赖 <piece> 的 trigger 称为「行动」。'
                '<action> 自身不声明独有字段——所有字段均与 <trigger> 一致。'
                '<action> 的 <condition>（若有）用于定义此行动何时可选（菜单可用性），<cost>/<target>/<content> 由玩家在点火时填入。'
                '<action> 的 <content> 槽位可以嵌套另一个 <action>、<effect> 或任意 content 形态——'
                '正如 <effect> 的 <content> 可以包含 <transfer>，嵌套不改变被嵌套概念自身的类型。'
                '判据：不依赖 <piece> + 由 <player> 做出 = action。'
                '<action> 与 <effect> 互为对偶：effect 依赖 <piece>、由状态边沿点燃；action 不依赖 <piece>、由玩家决策点燃。'
            ),
            'en': (
                'A <trigger> ignited directly by a <player> decision, existing independently of any <piece>. '
                '<action> shares the exact same structure as <trigger> (<condition> → <cost> → <target> → <content>); '
                'the distinction lies not in ontology but in naming convention: rulebooks and players call triggers that appear in turn menus, '
                'are performed by players, and do not depend on <piece>s, "actions". '
                '<action> declares no unique fields of its own — all fields match <trigger>. '
                '<action>\'s <condition> (if present) defines when this action is available (menu eligibility); '
                '<cost>/<target>/<content> are filled by the player at ignition time. '
                '<action>\'s <content> slot may nest another <action>, <effect>, or any content form — '
                'just as <effect>\'s <content> may contain a <transfer>, nesting does not change the nested concept\'s own type. '
                'Criterion: independent of <piece> + performed by <player> = action. '
                '<action> and <effect> are counterparts: effect depends on a <piece> and is ignited by a state edge; '
                'action is independent of any <piece> and is ignited by a player decision.'
            )
        }

        # Remove declaration and trigger references
        if '<declaration>' in c:
            del c['<declaration>']
        if '<trigger>' in c:
            del c['<trigger>']
        if 'actor' in c:
            del c['actor']

        # Update constraints to match trigger — all optional
        c['constraints'] = {
            'required': [],
            'optional': [
                '<condition>',
                '<cost>',
                '<content>',
                'target'
            ]
        }

    elif cid == 'effect':
        # Change extends to specifies
        c['specifies'] = '<trigger>'
        del c['extends']

        c['definition'] = {
            'zh': (
                '一种 <trigger>——绑定在 <piece> 上，携带 <condition> 监听状态变化，边沿激活后按 pipeline 结算。'
                '<effect> 是 armed 态的 trigger：它被 <resolve> 武装到 <piece> 上，此后由自身的 <condition> 决定何时点火。'
                '<effect> specifies <trigger>——不是 trigger 的子类型，而是一次参数填充：'
                'effect 填入 trigger 的全部槽位并附加「绑定于 <piece>、由 <condition> 边沿激活」的语义约束。'
                '含 <instant_effect>（一次机会）和 <continuous_effect>（持续武装）两个独立字段，可任意组合。'
                '与 <action> 互为对偶：action 不依赖 <piece>、由玩家点燃；effect 依赖 <piece>、由状态边沿点燃。'
            ),
            'en': (
                'A <trigger> bound to a <piece>, carrying a <condition> to monitor state changes; '
                'upon edge activation, follows the pipeline to settle. '
                '<effect> is a trigger in armed form: it is armed onto a <piece> by <resolve>, '
                'after which its own <condition> determines when to fire. '
                '<effect> specifies <trigger> — not a subtype of trigger, but a parameter binding: '
                'effect fills in all of trigger\'s slots and adds the semantic constraint '
                '"bound to a <piece>, edge-activated by <condition>". '
                'Contains two independent fields <instant_effect> (one chance) and <continuous_effect> (persistent arming), '
                'freely combinable. Counterpart to <action>: action is independent of <piece> and ignited by player; '
                'effect depends on <piece> and is ignited by a state edge.'
            )
        }

    elif cid == 'activation':
        # Change extends to specifies
        c['specifies'] = '<action>'
        del c['extends']

        c['definition'] = {
            'zh': (
                '一种特化的 <action>：<player> 的选择内容是从可用的 <effect> 池中激活某一个。'
                '<activation> specifies <action>——填入 action 的全部槽位并将 target 窄化为「选哪个 <effect>」。'
                '<player> 选择一个可用的 <effect> 并将其激活，该 <effect> 随即从静默（dormant）进入待结算（pending）状态，'
                '等待后续的 <resolve> 将其 <content> 应用到游戏 <state>。'
                '<activation> 不等同于 <resolve>——激活后效果可能排队等待，多个同时激活的 <effect> 需要通过优先级决定结算顺序。'
                '候选 <effect> 可能来自 <piece>（如研究牌上的活动能力）、<board>（如控制台自带的基础活动）、或其他游戏组件。'
            ),
            'en': (
                'A specialized <action>: the <player>\'s choice is to activate one <effect> from a pool of available effects. '
                '<activation> specifies <action> — fills in all of action\'s slots and narrows target to "which <effect>". '
                'The <player> selects an available <effect> and activates it, transitioning it from dormant to pending state '
                'to await subsequent <resolve> which applies its <content> to the game <state>. '
                '<activation> is not the same as <resolve> — after activation, effects may queue; '
                'multiple simultaneously activated <effect>s require priority ordering for resolution. '
                'Candidate <effect>s may come from <piece>s (e.g., activity abilities on research cards), '
                '<board>s (e.g., the innate activity on a console), or other game components.'
            )
        }

    elif cid == 'trigger':
        c['definition'] = {
            'zh': (
                '可执行规格的最小完整单元：<condition> → <cost> → <target> → <content>，'
                '按 pipeline（见 ontology/flow.json）依次结算。<trigger> 是体系的核心机器——'
                '所有游戏效果都跑在这同一台机器上。按点火源分为三种角色：'
                '(1) 纯 trigger——<condition> 边沿激活，规则自动执行，actor 为 null；'
                '(2) <effect>——specifies trigger，绑定在 <piece> 上、由 <condition> 边沿激活；'
                '(3) <action>——specifies trigger，不依赖 <piece>、由 <player> 决策点燃。'
                '三者共享完全相同的结构（condition/cost/target/content），区别仅在于点火源和称呼习惯。'
            ),
            'en': (
                'The smallest complete unit of executable specification: '
                '<condition> → <cost> → <target> → <content>, settled sequentially per the pipeline '
                '(see ontology/flow.json). <trigger> is the core machine of the system — '
                'all game effects run on this same machine. Three roles by ignition source: '
                '(1) pure trigger — <condition> edge-activates, rules auto-execute, actor is null; '
                '(2) <effect> — specifies trigger, bound to a <piece>, edge-activated by <condition>; '
                '(3) <action> — specifies trigger, independent of any <piece>, ignited by a <player> decision. '
                'All three share the exact same structure (condition/cost/target/content), '
                'differing only in ignition source and naming convention.'
            )
        }

    elif cid == 'event':
        # Update subclasses list
        c['definition'] = {
            'zh': (
                '游戏过程中发生的、被游戏规则所识别和定义的、对游戏 <state> 或流程有意义的、不可再分的原子事实。'
                '<event> 是流程本体的根概念——一切「发生了什么」都始于 <event>。'
                '子类包括 <transfer>、<trigger>、<resolve>、<shuffle>、<play>、<lose>、<gain>。'
                '<action> 和 <effect> 均 specifies <trigger>——'
                '它们共享 trigger 的完整结构（condition → cost → target → content），仅点火源不同。'
            ),
            'en': (
                'An atomic fact that occurs during gameplay, recognized and defined by game rules, '
                'meaningful to game <state> or flow, and not further divisible. '
                '<event> is the root of the procedure ontology — everything that "happens" starts from <event>. '
                'Subclasses include <transfer>, <trigger>, <resolve>, <shuffle>, <play>, <lose>, <gain>. '
                '<action> and <effect> both specify <trigger> — '
                'they share trigger\'s complete structure (condition → cost → target → content), differing only in ignition source.'
            )
        }

data['concepts'] = concepts

# Version bump
parts = data['meta']['version'].split('.')
parts[-1] = str(int(parts[-1]) + 1)
data['meta']['version'] = '.'.join(parts)

with open('ontology/concepts.json', 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)

print('Ontology updated successfully')
print(f'Version: {data["meta"]["version"]}')
print(f'Concepts remaining: {len(concepts)}')
for c in concepts:
    rel = c.get('extends', c.get('specifies', ''))
    if rel:
        rel_type = 'extends' if 'extends' in c else 'specifies'
        print(f'  {c["id"]}: {rel_type} {rel}')
