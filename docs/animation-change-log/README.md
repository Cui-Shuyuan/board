# 动画调整记录

这份日志记录**用户对讲规动画的每一次调整意见**，用于未来新会话归纳用户偏好、减少返工。

## 文件

- `entries.jsonl`：原始记录，append-only，一行一个 JSON。
- `digest.md`：从 entries 中人工/新会话归纳出的稳定偏好；新会话先读这里，需要追溯细节再读 `entries.jsonl`。
- 校验入口：

```bash
python3 tools/ops/check_animation_change_log.py
```

## 记录时机

每次用户提出动画调整意见后，在本次实现收尾时追加一条记录。

- 一条记录对应一次用户调整意见。
- 如果本次只提出、尚未实现，`status` 写 `pending`，`actual` 可留空。
- 后续实现 pending 项时，追加一条新的 `kind: "resolution"` 记录，并写 `resolves` 指向原 `id`；不要改写旧行。
- 只调整非动画内容（规则数据、工具链、纯文档）不进入本日志。

## 字段

必填：

| 字段 | 说明 |
|---|---|
| `id` | 稳定唯一 id，建议 `YYYY-MM-DD-<game>-<track>-<section>-<cue/scope>-<short>` |
| `date` | ISO 日期，如 `2026-10-13` |
| `game` | 游戏 id，如 `splendor` |
| `track` | `quick` / `full` / 未来 track |
| `scope` | `cue` / `section` / `track` |
| `section` | section id |
| `cue` | qualified cue id；section/track 级记录写 `null` |
| `before` | **修改前**：原动画/口播/状态如何 |
| `desired` | **希望修改成什么样**：用户的期望结果 |
| `reason` | **修改原因**：用户为什么这样要求、想避免什么误解 |
| `status` | `implemented` / `pending` / `rejected` |

可选：

| 字段 | 说明 |
|---|---|
| `actual` | 实际实现方式、关键状态/时间/视觉处理 |
| `commit` | Git commit，可使用短 hash |
| `tags` | 可归纳的标签，如 `quick`、`counterexample`、`teaching-order` |
| `source` | 对话/需求来源；backfill 可写 `backfill_yyyy-mm-dd` |
| `backfill` | 布尔值，表示是否为建立日志时回填的历史调整 |
| `kind` | 默认 `change`；后续解决 pending 时写 `resolution` |
| `resolves` | `kind=resolution` 时指向原记录 `id` |

## 写作要求

- `before` / `desired` / `reason` 用用户能理解的动画语言，不写内部实现术语。
- `actual` 才写实现细节和涉及文件。
- 不要只写“已调整”；要保留可比较的前后差异。
- 同一 cue 被多次调整时每次单独记一条，不合并历史。
- `digest.md` 只保留稳定偏好，不复制所有流水账。
