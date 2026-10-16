# 壁纸主题

每个主题放在独立目录中，应用启动时自动发现。不修改原始桌面项目。

目录包含 `theme.json`、`theme.js` 及所需素材。清单格式：

```json
{"id":"gravity","name":"引力之旅","description":"主题介绍","module":"theme.js"}
```

`id` 必须与目录名相同，不能使用保留名称 `default`。
`theme.js` 导出 `async function mount(host)`，在 host 中绘制动画，返回清理函数。
清理函数应停止动画、移除监听器并释放 GPU 资源。异步加载后需检查 host.isConnected。
使用相对模块路径和主题目录下的素材路径，避免依赖外部网络。
动画使用 `window.alcyoneAnimation.requestFrame/cancelFrame/now`，遵循桌面暂停策略。

新增主题后重启应用，在“设置 → 桌面与启动 → 壁纸主题”中选择。
引力之旅的渲染和公式素材来自用户提供的“新建文件夹 (2)”；保留场景模块内的许可声明。
