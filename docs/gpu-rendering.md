# WinUI 页面 GPU 渲染

当前客户端使用 WinUI 3。Microsoft.UI.Xaml 基于 Microsoft.UI.Composition，后者基于 DirectX；界面绘制与合成使用框架提供的图形硬件加速。

官方说明：https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer

2026-10-12 修改：设置页将没有渲染控制作用的“启用硬件加速”开关改为只读的“页面渲染：GPU 优先（系统自动管理）”。这表示框架的渲染策略，不是对当前机器 GPU 使用情况的检测结果，也不保证所有计算都在 GPU 上完成。

没有替换 WinUI 渲染器，没有新增强制 GPU 或指定显卡的接口。布局、数据处理、代理网络和核心加密仍由各自原有路径执行。

旧配置中的 `GuiItem.EnableHWA` 继续保留；页面没有为该字段创建双向绑定，打开或保存设置不会因为新的渲染说明而改写旧值。

验证结果：WinUI win-x64 自包含 Release 编译与发布成功，修改文件的 `git diff --check` 通过。测试包内 12 个必要程序、资源与组件文件存在；Xray、sing-box 和 ProxiFyre 与已有分流测试包的 SHA-256 一致。测试包不包含用户配置、日志或临时数据目录。

测试版入口：`D:\v2rayN-master\build-check\gpu-rendering\freedom.exe`。

尚未执行界面实际操作、截图或 GPU 占用测量；编译和资源检查不能证明特定机器运行时实际使用的显卡。
