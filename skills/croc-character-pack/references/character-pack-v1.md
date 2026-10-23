# Croc Token 角色包规范 v1

ZIP 根目录只能有 `character.json`、`preview.png` 和 `poses/` 下的八张 PNG；不要再套一层角色目录，不要放脚本或参考照片。

八个文件名：rest.png、crouch.png、work.png、recoil.png、relief.png、think.png、drink.png、jump.png。

动作图片均为单帧 1024×1024 PNG，真实 alpha 透明背景，人物朝左、背后朝右。完整身体，四边至少留 4 像素透明边距；不要地面、背景、文字、便便、奶瓶和阴影。奶瓶由软件提供；喝奶时前爪或手举到嘴边，预留道具叠加空间。

预览图为不超过 2048×2048 的静态 PNG，建议四列两行八动作总览，可标注动作中文名。它不参与桌面渲染。

以下清单可以直接使用，但 anchors 必须根据实际生成图片检查、修正，不能未经观察就声明坐标准确：

```json
{
  "version": 1,
  "pixel_art": false,
  "id": "my-character",
  "name": "我的角色",
  "canvas": [
    1024,
    1024
  ],
  "poses": {
    "rest": "poses/rest.png",
    "crouch": "poses/crouch.png",
    "work": "poses/work.png",
    "recoil": "poses/recoil.png",
    "relief": "poses/relief.png",
    "think": "poses/think.png",
    "drink": "poses/drink.png",
    "jump": "poses/jump.png"
  },
  "anchors": {
    "rest": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "crouch": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "work": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "recoil": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "relief": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "think": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "drink": {
      "feet": [
        604,
        973
      ],
      "outlet": [
        756,
        738
      ]
    },
    "jump": {
      "feet": [
        604,
        907
      ],
      "outlet": [
        756,
        672
      ]
    }
  }
}
```

- `version` 固定为整数 1；`canvas` 固定为 [1024,1024]。
- `id` 为 1–48 位小写字母、数字及短横线，首位为字母或数字；不能使用 builtin-croc。建议用英文角色名加版本。`name` 为 1–40 个可见字符。
- `pixel_art` 可选布尔值，像素画使用 true，保留最近邻采样，其他画风使用 false（默认）。
- `poses` 必须严格对应八个固定路径。
- 每个动作都记录 `feet`（脚底接触点中心）和 `outlet`（身体右后方的喷射连接点），单位是该张原图的像素，原点在左上。所有坐标须为画布内有限数值，附近必须有角色像素。
- 标准脚底为 (604,973)，起跳脚底为 (604,907)，比地面高 66 像素；工作和后坐的目标连接点为 (756,738)。优先让生图符合模板，再通过代码裁切、等比缩放及平移整理。实际位置稍有偏差时如实记录，经导入器按 feet 平移后用于渲染。切勿为了填标准坐标而虚报身体位置。
- 导入器自动把脚底对齐到基准并同步换算出口。修正后会裁掉身体的图片不合格。所有动作保持一致的角色体量；不要把每个姿势独立拉伸到同样高。
- ZIP 最大 32 MB，解压后总计不超过 48 MB，单文件不超过 8 MB。不接受重复文件、额外文件、加密包或符号链接；八个动作不能是完全相同的图片文件。
- 现有同 id、同内容包可重复使用；同 id 不同内容不会覆盖旧包，请改 id。
- 机械校验不能证明角色身份一致、姿势正确或出口符合解剖位置。生成者必须目视检查八动作总览和连接点标注图；工作台用户在安装前再次预览确认。

## 校验与打包

使用 Python 3.10+，图像处理需要 `python -m pip install -r requirements-croc.txt`。

```powershell
python character_pack.py pack 角色素材目录 我的角色.zip
python character_pack.py validate 我的角色.zip
```

八张素材及预览和清单应实际存在于素材目录，再运行打包。工具仅校验和打包，不会调用生图或把不合格图片伪装合格。
