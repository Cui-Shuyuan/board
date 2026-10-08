# animation/archive

这里存放**已经完成使命的一次性迁移脚本**，只用于历史追溯，不是日常入口。

## migrate_time_anchors_v2.py

- 用途：把旧版 Splendor full 源数据里的裸 `at` 秒迁移为顶层
  `time_anchors` + 事件 `anchor`/`offset`。
- 状态：迁移已于 `38971d4` 全量提交；当前源数据保留 `anchor`，编译产物仍输出
  数值 `at`，当前格式不再需要重复执行该脚本。
- 归档原因：如果误运行，可能基于已经变化的数据重新写入 anchor，污染当前
  源数据。

## migrate_object_targets_v2.py

- 用途：把旧版显式 `zone` / `overlay` / `source` / `destination` 字段迁移为
  `target: {space: entity|screen, ...}` 接口。
- 状态：迁移已完成，当前 v2 源数据统一使用 `target` 接口。
- 归档原因：脚本是源数据一次性迁移器，不属于运行时或日常编译链；保留在顶层
  会被误认为仍在使用的入口。
