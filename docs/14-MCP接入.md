# MCP 接入：第一批实现

最后更新：工作区未提交（2026-11-18）

## 定位

MCP server 按 [`04-插件与内部结构.md` §4](04-插件与内部结构.md) 作为适配器插件处理：server 提供外部能力，Lilara 将其 `tools/list` 映射成 `ITool`，调用时经 `tools/call` 转发。工具表仍由统一的 `ToolTable` 装配；没有平行的 MCP 工具注册表。

当前实现支持 `tools/list` / `tools/call`，传输为本地 `stdio` 与远程 **Streamable HTTP**。使用官方 `ModelContextProtocol.Core` SDK，SDK 类型封装在 `Lilara.Core.Mcp.McpHost` 内，不进入模型适配器或控制面契约。

## 配置与启动

配置路径为 `<数据根>/mcp.json`。`enabled` 总开关默认 `false`；每个 server 的 `enabled` 也默认 `false`。登记 server 不会启动子进程。用户显式运行 `mcp on` 后，或执行 `mcp reload` 时，核心才按当前配置连接已启用的 stdio server。`mcp test <id>` 临时连接、读取工具清单后断开，不改变工具表。

最小配置示例：

```json
{
  "version": 12,
  "enabled": true,
  "servers": [
    {
      "id": "filesystem",
      "enabled": true,
      "transport": "stdio",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "E:/work"],
      "timeoutSeconds": 60,
      "toolPermission": { "write_file": "Writing" }
    }
  ]
}
```

远程服务登记示例（总开关及 server 均需显式启用）：

```json
{
  "id": "remote-docs",
  "enabled": true,
  "transport": "http",
  "endpoint": "https://mcp.example.com/mcp",
  "bearerTokenEnvironmentVariable": "MCP_DOCS_TOKEN",
  "allowPrivateNetwork": false,
  "timeoutSeconds": 60
}
```

认证 token 只从进程环境变量读取；`GET /mcp` 只显示 endpoint（去掉 query）和是否配置认证，不回显 token。认证只允许 HTTPS。HTTP 明文仅能在显式 `allowPrivateNetwork=true` 时使用，适合本机/内网开发端点；该选项同时放开 loopback 与私网目标，但 metadata 地址始终拒绝。HTTP 重定向关闭，不跟随 3xx 到另一主机。

命令形态：

- `mcp list`
- `mcp set <id> --transport http --endpoint <URL> [--token-env <环境变量名>] [--allow-private on|off] [--timeout <秒>] [--enabled on|off]`
- `mcp set <id> --transport stdio --command <程序> [--arg <值>]… [--cwd <目录>] [--timeout <秒>] [--enabled on|off]`
- `mcp test <id>`
- `mcp on` / `mcp off`
- `mcp reload`
- `mcp remove <id>`

`mcp.test <id>` 会真实建立一次连接、读清单并断开；`mcp reload` 会连接启用项并将工具加入工具表。

## 工具契约

- provider 工具名由 `McpNames.Compose(serverId, remoteToolName)` 生成：server id 前缀、非法字符替换、长名称加稳定哈希，最终符合 `[A-Za-z0-9_-]{1,64}`。
- 工具表按工具名 ordinal 排序；重名在装配期拒绝，错误包含冲突来源。
- `Description` 与 `inputSchema` 原文来自 MCP 清单。单工具 schema 上限 64 KiB，描述上限 16 KiB，每 server 最多 128 个工具。
- 权限类别由 `toolPermission` 配置映射；未配置或值无效时取 `Writing`，符合 `04` §5 的默认类别。MCP 工具仍经过现有权限 Gate；HTTP 远程工具还要求会话 `network=all`，设备级启用只建立连接，不替会话授权出网。
- 文本、图片、嵌入资源和结构化内容映射到 canonical block。图片/二进制资源通过只写入内容寻址 blob 的 `EnsureStored` 句柄落库；不支持的内容类型会在结果中明确提示，不静默丢弃。`isError` 仍作为工具结果内容保存。
- 一次调用最多等待配置的 `timeoutSeconds`。调用期间同一 server 只允许一条在途调用；竞争调用收到明确的忙碌结果。
- server 掉线时工具仍留在工具表；调用结果明确报告离线。只有显式 reload 才会重建表。

## 进程与凭据

MCP 宿主由 CLI 的 `CoreHost` 或嵌入宿主的 `KernelRuntime` 持有，生命周期与核心一致。正常释放会关闭 SDK client，SDK `DisposeAsync` 会关闭 stdio 子进程。启动连接在 CLI 后台执行；失败会进入状态快照，不阻塞控制面启动。

stdio 是代码执行能力。总开关和单 server 开关默认关闭；配置文件损坏或不存在时回到关闭。子进程不继承父进程的完整环境，避免把 provider API key 顺带交给 MCP server；SDK 默认运行环境变量与配置显式列出的变量会传入。配置值可写 `${NAME}` 引用进程环境变量，未定义时记录配置问题并使用空值。不要把秘密直接写进 `mcp.json`。

`GET /mcp` 返回 server 状态、工具、配置问题与有界 stderr 尾部；同时返回脱敏后的连接配置（endpoint 去 query/user-info、token 只给环境变量名和 `hasBearerToken`），配置已保存但尚未 reload 时标记 `pending-reload`，未启动项标记 `registered`。控制命令包括 `mcp.list`、`mcp.configure`、`mcp.upsert`、`mcp.remove`、`mcp.reload`、`mcp.test`。工具表变化会使已缓存的 Stepper 失效；状态变化通过 `core` topic 的 `mcp.changed` 事件广播。

## 边界与后续

仓库内有本机 Streamable HTTP loopback fixture 集成测试，覆盖 legacy `server/discover` 回退、initialize、tools/list、tools/call、调用超时/取消、显式 reload、停机错误和状态快照；另有 endpoint policy、metadata IP 拦截、Bearer header 构造与脱敏、命名、配置规范化、工具排序/重名单测。stdio 的 SDK 握手、清单/调用、文本/图片、`isError`、取消与 dispose 子进程曾以临时本机 server 手工验证，尚未有进仓的 stdio fixture。

尚未实现：MCP prompts、resources、`notifications/tools/list_changed`、进程内自动重连、MCP progress 事件、OAuth 授权流程和按会话启停工具。当前认证仅支持通过环境变量注入的 Bearer token。HTTP Streamable transport 使用 SDK 自带 HTTP 会话处理，连接断开后目前需显式 `mcp.reload` 重连。HTTP 工具调用另受会话网络轴限制，只有 `network=all` 才允许调用；设备级 MCP 开关不提升会话权限。`prompts` 是用户选择并可传参数的模板，不能直接塞进稳定提示词贡献槽；若实现，应成为控制点流程，由用户选取、填写参数后再送入会话。
