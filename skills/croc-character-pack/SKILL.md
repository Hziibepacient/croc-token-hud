---
name: croc-character-pack
description: 根据一张或多张参考图，使用 Codex 内置生图制作 Croc Token 的八动作透明角色包，完成定位检查、校验和 ZIP 交付。用于新增可替换桌面角色，不用于 ChatGPT Pets 或修改 Token 统计。
---

# Croc Token 角色制作

读取 [完整生成流程](references/generation.md)，按其中的基准图、逐动作生图、定位与视觉验收流程完成任务。角色格式以 [角色包规范](references/character-pack-v1.md) 为准。

使用可用的内置生图能力；如环境提供 imagegen 技能，遵循该技能。照片/插画参考保留识别特征，八张资产分别生成。不要把非鳄鱼角色套进鳄鱼专属局部动画。

通过本技能的独立工具 `scripts/character_pack.py` 校验与打包，Python 依赖为 Pillow 10.4–12.x，可用 `python -m pip install -r scripts/requirements-croc.txt` 安装（路径相对此技能目录）。该脚本不调用生图、不执行包内代码。用 `pack <素材目录> <输出.zip>`，再用 `validate <输出.zip>`。

必须目视检查身份、八动作、脚底与喷射点；不以机械检查替代视觉判断。失败保留素材并明确问题，不宣称已合格。最终交付本地八动作总览和实际 ZIP 文件。
