# 本地证据审计

`evidence_audit` 读取 DebugSaveAllPackets SQLite 和游戏 master，生成来源指纹、请求响应配对、Pioneer 可见观测、动作正文及机制字段取值清单。它不认证完整游戏规则，也不生成完整育成模拟器。

```json
{
  "name": "evidence_audit",
  "arguments": {
    "packetsPath": "<包库文件或目录>",
    "masterPath": "<master.mdb>",
    "outputDirectory": "<新目录>"
  }
}
```

```json
{
  "name": "evidence_validate",
  "arguments": {
    "reportPath": "<新目录>/audit.json"
  }
}
```

输出目录须为新目录或空目录。审计完成时检查 `summary.validation`，输入错误由 MCP 工具错误返回。源码通过独立 MessagePack 和 Microsoft.Data.Sqlite 读取数据，不依赖 Host runtime。

## 适用口径

用户已确认使用本地材料，不要求 master 与捕获包版本严格一致。版本、区服来源和同轨迹版本差异作为适用域元数据保留，不单独阻断本地研究；`VersionBinding.Status` 和 `RegionVerification` 未经证实时保持 `Unverified`。

机制解释和轨迹完整性仍需核验。`ObservedOnly` 只表示捕获到观测；开局、终局和连续回合号不能证明同一回合内没有遗漏动作。当前工具不会生成规则认证的 `Passed`，也不会认证手工改写为 `Passed` 的报告。

## 本轮实包结果

最后一次完整扫描输出位于 `artifacts/evidence/local-audit-05`。统计如下：

| 项目 | 结果 |
| --- | ---: |
| 包库 | 4 |
| 捕获记录 | 61,477 |
| Pioneer 捕获记录 | 59,947 |
| 规范化 Pioneer 观测 | 29,878 |
| exchange | 30,892 |
| 已配对 exchange | 30,585 |
| 仅请求 / 仅响应 | 249 / 58 |
| 冲突 / 重复捕获 / 解码失败 | 0 / 0 / 0 |
| 可关联育成轨迹 | 79 |
| 观察到开局 / 终局的轨迹 | 79 / 78 |
| 回合跳跃记录 | 14 |
| 无法关联到 run 的局部观测 | 4,085 |
| master 用户表 | 638 |
| master effect / condition 等取值域 | 529 |
| 导出的相关 master 行 | 231,596 |

所有可关联轨迹在本轮材料内均来自单一包库，按源行 ID 排序。无法关联 run 的局部观测包括没有角色身份字段的响应，不能将 4,085 条全部解释为漏包。回合跳跃也需要区分缺包与游戏自动推进。

本轮 Pioneer 捕获头中的 app 版本为 `2.32.5`，资源版本为 `10021210:Tcxkvfn+yCx3`；这些字段本身不证明 master 对应关系或区服。

最终扫描采用用户确认的本地材料口径，并包含严格对象形状检查。05 的阻断项仅为未知机制、轨迹完整性和孤包；没有将版本/区服未证明列为阻断。原始运行记录没有被改写。

## 只读检查

读取使用 SQLite `ReadOnly`、私有缓存和延迟事务。非空 WAL 会被拒绝，避免把主文件哈希误当完整数据库快照。审计前后比较源 SHA-256 和长度；最终 05 与先前独立核对的 04 来源指纹也相同。以下五个源全部一致，且没有非空 WAL。

| 源别名 | 字节 | SHA-256 |
| --- | ---: | --- |
| packets-0001 | 20,480 | `cbe5e392fce0397f5a59f6fcb885f6865b08919a2566f6c3b1a33e1622bc7e09` |
| packets-0002 | 375,709,696 | `9caf1bdf92e72ec61f6d900ca5f026c1068007611cbf736e33c164625157d0e1` |
| packets-0003 | 35,450,880 | `a6acedd7ae0fd3e0a4910055d2ec785af3049f20f52243489953a46b03404f71` |
| packets-0004 | 384,102,400 | `058d9ce35589ddc9900e0d6574c469645406ab621ef83c36daaf6f7271181845` |
| master | 45,785,088 | `4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886` |

报告使用源别名和哈希，不打印捕获库的账户文件名。没有启动监听、操作游戏、部署或修改源数据库。

## 配对与字段边界

- 跨库使用捕获生成的 GUID `request_id` 与 endpoint 配对；不同正文或参与者身份冲突不会任取一条结果。
- 只有同 exchange、同正文及身份/版本元数据的重复捕获才合并。源引用仍保留在 exchange 清单中。
- run 身份由捕获参与者与 `single_mode_chara_id` 共同派生，不以文件名代替参与者。跨库排序要求明确捕获时间；时间缺失、倒退或跨库同刻时报告顺序未验证，不按回合号猜测重排。
- 标量缺失、JSON null、非法类型和真实整数 0 分开表示。已知 data/common/chara 对象的错误形状会报告错误，不转为缺失字段。
- 观测保留完整 data 区域，动作保留完整请求对象，再移除已识别的隐私字段。新游戏字段不会因动作白名单被丢弃。效应/条件取值域标为 `Uninterpreted`，数量不等于已支持机制数。

`local-audit-01` 至 `local-audit-03` 是研究中间产物，可能包含请求中的 IP、keychain 或 Steam 标识，**不发布**。最终 05 的请求递归扫描确认 `viewer_id`、`ip_address`、`keychain`、`steam_id` 均未保留。过滤规则覆盖已识别字段；没有证明所有未知字段均已脱敏。源指纹、run 哈希和局部数据也不能视为匿名化保证，派生证据应按本地研究数据处理。

## 验证

```powershell
dotnet test tests/UmaAiSharp.Tests/UmaAiSharp.Tests.csproj --no-restore --filter 'FullyQualifiedName~Evidence|FullyQualifiedName~Goal|FullyQualifiedName~CoreSimulation' --logger 'console;verbosity=minimal'
```

本轮 48 项对应测试全部通过，覆盖跨库配对与冲突、重复捕获、缺失与零的区分、包络形状、字段保留、版本差异、文件只读/WAL、诊断脱敏、严格目标 JSON、目标统计和固定样本多线程运行。实际运行未知子命令返回明确报错和退出码 `2`。最终全项目验证及 NuGet 在线审计限制见实施报告；这些测试不等于完整 Pioneer 机制已实现或已通过实机校验。
