# cut → Sticky 0.7.0 迁移验收记录

日期：2026-10-30。源功能基线：cut 0.16.0。实现、设置、资源、固定依赖和功能测试已迁入 Sticky，独立构建及发布不引用 cut 工作目录。

## 已完成的验证

| 检查 | 结果 |
| --- | --- |
| .NET 10 完整解决方案构建 | 通过，0 警告、0 错误 |
| Sticky 回归 | 101 项通过，0 失败；包含设置、旧配置导入、截图会话和同进程窗口交互 |
| 截图基础套件 | self、qr、window、ocr、translation、ai、vision、annotation、scroll、auto-scroll、scroll-optimization、scroll-permissive、recording 共 13 组通过 |
| 原生交互套件 | scroll-native、auto-scroll-native、recording-native、recording-ui-native 共 4 组在空闲桌面连续通过，0 失败 |
| 实际便携 ZIP | 真实启动、完整升级事务、失败试启动与回退共 3 项通过，便签／分组／图片字节保持一致 |
| 发布工作流 | 13 项模拟工作流检查通过 |
| 发布 API／凭据 | 6 项模拟检查通过，无真实发布请求 |
| ZIP 格式 | Windows PowerShell 的标准路径、Unicode 内容、禁止覆盖和排除个人数据检查通过 |

最终便携包：`dist/Sticky-0.7.0-win-x64-portable.zip`，约 291 MiB。

SHA256：`55cc147bb742b7ce823269aa1dd31767ef8d044a17ae5582ec7cea7d74555dae`。

最终实际包的隔离验收：`.test-data/portable-update-379f522a20624fba937f854112fcfb23/result.json`。原生交互补验结果：`.test-data/capture-native-validation.json`，已核对便携包 SHA256 与实际包验收结果一致。构建与回归日志：`.tools/capture-package.log`、`.tools/capture-final-tests.log`、`.tools/capture-native-final.log`、`.tools/capture-portable-tests.log`。设置的合成预览位于 Sticky 测试输出目录。

## 原生交互复测的状态

2026-10-30 在用户确认空闲的解锁桌面上完成最终补验，四组原生交互测试连续全部通过。连同已通过的 13 组基础套件，17 组截图／录制套件均已验收通过。

- 手动长截图：真实 GDI 采集、双向位移、工具栏截图排除及目标窗口移动保护。
- 自动滚动：上下方向 × 三档速度逐像素输出、边界停止、继续采集、用户接管、全局 Esc、鼠标离开及失焦保护。
- 原生录制：WGC 5／60 FPS、GDI 10 FPS，实际尺寸、捕获排除、静止画面时长及停止响应。
- 录制交互：真实鼠标停止、全局快捷键、60 秒上限自动停止；预览位于独立进程窗口前方、正常层级恢复、裁剪鼠标拖拽、逐帧操作、导出、重录和快捷键释放。

测试结束后恢复了鼠标位置。此前受桌面操作干扰的失败日志保留，最终通过日志为 `.tools/capture-native-final.log`，未放宽输入接管、遮挡或失焦保护。以后完整复验时保持桌面空闲，然后运行：
```powershell
./scripts/test-capture.ps1 -NoBuild -Native
```

正式发布入口强制带上原生交互测试；失败会阻止后续发布。当前版本的自动化验收已通过，尚未执行真实远端发布。

## 覆盖边界

AI／翻译使用模拟 HTTP、SSE 和合成输入验证协议、取消、迟到结果与凭据处理；未使用个人 API 密钥访问真实服务。真实混合 DPI 多屏、HDR、受保护视频及不同浏览器／聊天软件仍需对应硬件和应用验收。实际网页样本回放与基准测试需要另行提供原有样本，不纳入默认套件。
