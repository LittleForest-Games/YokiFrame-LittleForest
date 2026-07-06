# Workbench Manifest Schema（YF1b 定义，加载推迟）

本文件定义 YokiFrame Workbench 声明式 Kit descriptor 的 manifest schema 与加载契约。

**状态**：YF1b 仅定义 schema。实际加载机制等取得可构建 Tauri 前端源码后再实现（阻塞 YF4，不阻塞 YF2/YF3）。

## 背景

上游 `TauriRuntime~/dist/` 只有编译后产物（.js/.css/.html），无 Tauri/Rust 源码目录，无法仅凭当前包完成可复现构建。规划文档 §4.2 已明确："若上游暂未发布 Tauri 源码，可以先完成可靠 FileBridge、AI 查询和 GM 协议"。

因此 YF1b 只做 manifest schema 定义与 `IKitManifestProvider` 接口预留，不实现 Workbench 前端的 manifest 加载逻辑。

## Manifest 文件位置

```text
.yokiframe/engines/<engine-id>/manifests/<extension-id>.json
```

示例：`.yokiframe/engines/unity-editor/manifests/littleforest.json`

- `<engine-id>`：与 engine registry 一致的引擎标识符（如 `unity-editor`）。
- `<extension-id>`：与 `[YokiFrameCommandBridgeExtension(ExtensionId)]` 属性一致的扩展标识符。
- 一个扩展对应一个 manifest 文件。

## Manifest Schema

```json
{
  "schemaVersion": 1,
  "extensionId": "littleforest",
  "engineId": "unity-editor",
  "kits": [
    {
      "kit": "LittleForest",
      "title": "Little Forest",
      "snapshot": "state",
      "sections": [
        { "type": "metrics", "path": "data.context" },
        { "type": "table", "path": "data.systems" },
        { "type": "status", "path": "data.control" },
        { "type": "json", "path": "data.lastError" }
      ]
    }
  ]
}
```

### 字段说明

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `schemaVersion` | int | 是 | schema 版本，当前固定为 `1`。 |
| `extensionId` | string | 是 | 扩展唯一标识符；必须与 `[YokiFrameCommandBridgeExtension]` 属性值一致。 |
| `engineId` | string | 是 | 目标引擎标识符（如 `unity-editor`）。 |
| `kits` | array | 是 | 该扩展声明的 Kit descriptor 列表。 |
| `kits[].kit` | string | 是 | Kit 名称；必须与通过 `RegisterHandler` 注册的 `IKitCommandHandler.KitName` 一致。 |
| `kits[].title` | string | 是 | Workbench 前端显示标题。 |
| `kits[].snapshot` | string | 是 | 该 Kit 默认展示的 snapshot 名称；必须与 `IKitSnapshotPublisher.SnapshotName` 一致。 |
| `kits[].sections` | array | 是 | Kit 页面分区列表，按顺序渲染。 |
| `kits[].sections[].type` | string | 是 | 组件类型，必须属于白名单（见下）。 |
| `kits[].sections[].path` | string | 是 | snapshot JSON 中的数据路径（dot 分隔），如 `data.context`。 |

## 白名单组件类型

| `type` | 说明 | 数据来源 |
| --- | --- | --- |
| `metrics` | 键值对指标 | snapshot.data.\<path\> |
| `status` | 状态标识（ok/warn/error + 摘要） | snapshot.data.\<path\> |
| `table` | 表格行 | snapshot.data.\<path\> |
| `timeline` | 时间线事件 | snapshot.data.\<path\> |
| `json` | JSON tree | snapshot.data.\<path\> |
| `command` | 命令表单 | command catalog 中该 kit 的 actions |

非白名单 `type` 视为 manifest 无效（见加载契约）。

## 加载契约（定义，不实现）

- Host 在 `WriteEngineRegistry()` 时扫描 `manifests/*.json`，写入 engine registry 的 `extensions` 字段。
- Workbench 前端通过 `System/get_engine_registry` 读取 manifest，渲染白名单组件。
- **manifest schema 不匹配时**：Host 记录警告并跳过该 manifest；Workbench 显示 "extension manifest invalid"。
- **缺失 `schemaVersion` 或值非 `1`**：manifest 无效，跳过。
- **`kits[].kit` 与已注册 handler 的 KitName 不匹配**：manifest 中该 kit 条目跳过（不影响其他 kit）。
- **`sections[].type` 不属于白名单**：该 section 跳过（不影响其他 section）。

## Host 侧 manifest 发现接口（仅定义）

`IKitManifestProvider` 接口在 `Core/Runtime/CommandBridge/IKitManifestProvider.cs` 预留，方法暂不实现：

```csharp
public interface IKitManifestProvider
{
    string ExtensionId { get; }
    string BuildManifestJson();
}
```

扩展可通过 `ICommandBridgeExtensionContext` 注册 `IKitManifestProvider`（接口预留，注册方法暂不实现）。实际 manifest 写入逻辑等 YF1b 完整实现时（取得可构建前端后）再做。

## 验证（仅 schema 层）

- manifest schema JSON 可被 JSON 解析器解析。
- `schemaVersion` 字段存在且为 `1`。
- `kits` 数组中每个 kit 有 `kit`/`title`/`snapshot`/`sections`。
- `sections` 中 `type` 属于白名单（`metrics`/`status`/`table`/`timeline`/`json`/`command`）。

## 后续（YF1b 完整实现，取得可构建前端后）

- 实现 `IKitManifestProvider.BuildManifestJson()`。
- 在 `ICommandBridgeExtensionContext` 增加 `RegisterManifest(IKitManifestProvider)` 方法。
- Host `WriteEngineRegistry()` 扫描 manifest 并写入 engine registry。
- Workbench 前端实现 manifest 加载与白名单组件渲染。
