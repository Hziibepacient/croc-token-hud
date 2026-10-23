# Croc Token HUD 1.4.0-beta.1

Windows 桌面角色源码预览版。

- 新增本地角色工作台：ZIP 导入、八动作与喷射预览、确认后安装。
- 右键更换已有形象并记住选择；保留原版鳄鱼动效。
- 提供 Codex 完整生成指令、制作工具包、可选 skill 和八动作格式校验。
- 工作台增加三步操作引导；416 像素预览、96 像素缩略图，隐藏时暂停绘制。
- 重写 README，保留作者及协作者署名，说明本地额度与响应平均吞吐的边界。

下载 `CrocToken-preview-source.zip` 后完整解压，安装 Python 3.10+，双击 `StartCrocDesktop.cmd`。新增角色需先运行 `python -m pip install -r requirements-croc.txt`。本包不包含 Python，不是免安装版。

`Codex-character-maker-kit.zip` 用于交给 Codex 制作新角色；里面包含指令、规范、校验脚本与可选 skill。每个 ZIP 附 SHA256 文件。

发布前自动运行 Python 测试、原鳄鱼与导入角色原生窗口自检，并验证实际解压后的源码能编译和运行。真实日志、账号信息、机器配置及原始参考图不包含在包内。
