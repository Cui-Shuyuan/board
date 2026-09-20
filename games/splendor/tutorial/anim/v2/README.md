# v2 动画管线

- 源：`full.anim.json`
- 编译产物：`full.compiled.json`
- stage 源：`_stage/*.stage.json`
- 几何唯一源：`scripts/anim_geometry_v2.py`

## 命令

```bash
python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/full.anim.json
python3 scripts/compile_animation_v2.py --game splendor --track full
python3 scripts/compile_animation_v2.py --game splendor --track full --check
python3 scripts/check_anim_v2.py --game splendor --track full
python3 scripts/validate_anim_rules_v2.py --game splendor --track full
python3 scripts/check_unity_scripts.py
```

Unity 采样：

```bash
./scripts/dump_anim_v2.sh --game splendor --track full
python3 scripts/check_anim_v2_sample.py --game splendor --track full
```

运行时只读 `full.compiled.json`，不再解析 selector、不再计算镜头/格位。
