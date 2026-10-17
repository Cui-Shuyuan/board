# 规则问答程序接管 02：局部流程、规范索引与有依据的成员枚举

日期：2026-10-17
依赖：01
提交：本阶段独立提交

## 1. 程序实际接管了哪一步

- 程序现在按游戏规则 snapshot 构建流程结构索引：每个节点有 `canonical_path`、原始 JSON 来源路径、父路径、直接子路径、声明顺序、loop、condition、do_after、optional 和 kind。
- `flow` 的 `entity + scope` 由程序解析。`scope=local` 返回目标正文、直接子节点、祖先条件/循环和父级顺序；`scope=game` 才返回整局；无 entity 的 local 返回 invalid，不会静默退回整局。
- 同名局部 id 不再作为唯一身份：两个 owner 下的 `step` 返回两个 canonical path 候选；只有完整路径才能 exact resolve。
- `members` 只读取显式 `members/items/options/slots` 集合或 schema 的 `specifies/extends/instance_of` 关系；description 中的 `<concept>` 文本引用不会被当作 membership。
- `list` 与 `members` 支持 offset/limit。第一页带 `has_more` 和 continuation，不宣称全集；合法空集合带 complete + total_count=0。

## 2. 模型少做了哪一步

在新候选提示和 tool schema 中，模型只需提交：

```json
{"relation":"flow","scope":"local","entity":"harvest_feeding","include":["children","position","conditions"]}
```

或：

```json
{"relation":"members","entity":"habitat","offset":0,"limit":20}
```

程序完成路径消歧、父子/顺序/循环/条件读取、类型成员筛选和分页；模型不再需要：
- 从整局 `game` 流程里自己找目标；
- 把同名局部 id 当全局 id；
- 在目录里猜成员或把文本提到当作全集；
- 自己把第一页拼成全集。

本阶段未运行真实模型同轮对照，因此“少调用/少 token”的效果为未知，不宣称质量或延迟收益。

## 3. 契约与实现

### FlowSlice

`QueryPayload(type=flow_slice)`，`Content` 是目标原始节点；`Data` 包含：

- `scope`、`target`、`ancestors`、`parent`、`siblings`、`children`、`child_count`、`can_expand_children`
- 每个骨架节点带 `canonical_path/id/name/kind/index/optional/do_after/loop/condition/child_paths`
- 直接子节点带完整 `content`，祖先只带理解局部所需的 loop/condition；更深读取由后续 continuation/同路径查询完成。

### MemberSet

`QueryPayload(type=member_set)`，`Data` 包含：

- `collection_kind`（`explicit:members/items/options/slots` 或 `schema_type_relation`）
- `members[{canonical_path,id,name,source,content}]`
- `total_count/offset/limit/has_more/membership_proven`

`membership_proven=false` 时 coverage 为 partial，附 `no_rule_coverage` omission 和定义 payload；绝不能把空 members 称作全集。

### 索引一致性

- 索引在 `RulesSnapshotScope.Current` 上缓存，key 绑定游戏与规则版本。
- 原始 JSON 路径与 canonical path 分开保存；canonical path 由父路径 + declared id 递归生成，重复来源追加稳定后缀。
- 请求内同一 snapshot 的索引不混合热更新后的文档；切换 snapshot 后重建。
- 字符串关系/引用只参与 schema membership 判定，不做递归文本归属推断；流程索引不按字符串 ref 展开，因此不会无限循环。

## 4. 行为测试

新增 `FlowScopeAndMembersTests`：

- 两个父流程下同名 `step`：候选路径互不串位，完整 canonical path 才 exact。
- local flow 返回目标、直接子步骤、父循环/条件；不夹带无关行动格；无目标 local 为 invalid。
- 同 snapshot 流程索引缓存；规则热更新后索引重建；飞行中的旧 snapshot 仍返回旧版本。
- catalog 两页重建无重复、无遗漏。
- 成员枚举区分显式集合、schema 继承/实例、纯 description 提到、未知全集和合法空集合。
- 成员第一页 coverage=partial，continuation offset 指向第二页。
- 真实游戏契约：
  - Agricola `harvest_feeding` local flow 命中 `game/round_loop/harvest/harvest_feeding`，祖先含 `round_loop/harvest`；
  - Wingspan `habitat` members 通过 schema `specifies` 返回 `forest_habitat/grassland_habitat/wetland_habitat`，不把 `habitat` 自己当成员。

测试结果：后端 `238/238` 通过。

冻结查询实际输出见 [02-frozen-query-contracts.json](02-frozen-query-contracts.json)：

- Agricola flow local：`resolution=exact`、`coverage=complete`、一个 `flow_slice`、无 omission。
- Wingspan members：`resolution=exact`、`coverage=complete`、3 个声明成员。
- Wingspan catalog：`catalog_page` complete，带分页元数据。

## 5. 候选提示和 schema 变更

- `execute_plan` relation enum 增加 `members`。
- schema 增加 `scope/offset/limit/include`，并显式说明 flow local 必须有 entity。
- legacy candidate prompt 增补 flow scope、members 与分页规则。
- 原 `a992cfe` legacy prompt SHA `859260e5…f1343d5` 仍作为冻结基线保留；新候选 prompt 不得再冒用该 SHA。当前候选 prompt SHA 为 `56cfe10485912c40efe97297f6255b100d6b754fa470fc0dcfd8d041256c2161`。
- 新 tool schema 指纹为 `c245c7630926`。

## 6. 剩余不确定性

- `membership_proven=true` 只证明当前 snapshot 的声明数据和 schema 关系完整，不证明规则书中未录入的成员存在。
- 局部流程只保证目标 + 直接子节点与必要父级；更深层结构与跨引用内容仍需 continuation 或后续查询。
- 没有真实模型同轮冒烟；模型是否按新 schema 正确使用 `scope`/`members`，以及是否减少机械调用，尚未验证。
- 未修改 program 状态机；program 仍只是实验路径。
