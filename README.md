# OfficeCLI

[![Build](https://github.com/raystyle/OfficeCLI/actions/workflows/build.yml/badge.svg)](https://github.com/raystyle/OfficeCLI/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/raystyle/OfficeCLI)](https://github.com/raystyle/OfficeCLI/releases/latest)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

面向 AI agent 的 Office 文档命令行:`.docx` / `.xlsx` / `.pptx` 的读写、校验与渲染。单文件自包含二进制,无需安装 Office;内置 HTML/PNG 渲染引擎,让 agent 能「渲染、查看、修复」闭环。

## 项目介绍

- **是什么**:Office 文档操作的 CLI 与 MCP 双面;路径寻址(`/body/p[3]`、`/slide[1]/shape[@id=...]`)、CSS 式 query、schema 驱动 help、resident 常驻加速、watch 实时预览。
- **为谁**:AI agent(Claude Code / Codex / Cursor 等)与人共用同一命令面;`--json` 全程机器可读。
- **分工**:本仓管产品本体;**ark** 管舰队安装/升级/验收落地;**omc** 管分发资源与多云运维。
- **血统**:上游 [iOfficeAI/OfficeCLI](https://github.com/iOfficeAI/OfficeCLI),本 fork(raystyle)自 2026-09-15 起接管构建、发布与自更新分发。

## 部署

```bash
# 舰队(推荐):ark 管辖安装与升级
ark install officecli

# 直下(独立分发域;stable 滚动段自 1.0.153 起,版式段即用)
curl -O https://officecli.ohmygh.com/officecli/1.0.152/officecli-linux-x64   # 或 -win-x64.exe / -mac-arm64
chmod +x officecli-linux-x64 && ./officecli-linux-x64 --version

# 安装脚本(镜像优先,GitHub release 回落;PATH 注册 + 幂等)
curl -fsSL https://raw.githubusercontent.com/raystyle/OfficeCLI/main/install.sh | bash
```

- **平台矩阵**:linux-x64/arm64、linux-musl(alpine)x64/arm64、osx-x64/arm64、win-x64/arm64,全部单文件自包含。
- **校验**:直下资产可验 `sha256sum officecli-linux-x64`,期望值见 release 附带 `SHA256SUMS`(镜像同目录带 `.sha256` 边车)。
- **五端注意**:Windows 原生跑 shell 脚本用 Git Bash;WSL 与宿主同机时注意 CPU 争用;alpine 变体非静态,需 musl 动态链接器。
- **自升级**:内置更新器指向 fork 分发(镜像优先,GitHub release 回落);`officecli install` 一步装二进制 + skills + MCP。

## 配置

环境变量:

| 变量 | 作用 |
|---|---|
| `OFFICECLI_ENVELOPE` | 机器信封形:`compat`(ok 与 legacy success 并存,缺省)\|`strict`(仅 ok;错误折字符串 + stderr 单行 `{code,message,cta}`) |
| `OFFICECLI_RESIDENT_FLUSH` | resident 落盘策略:`each`(每命令)\|`auto`(自适应 2-10s)\|`<秒>`\|`off` |
| `OFFICECLI_RESIDENT_IDLE_SECONDS` | resident 空闲退出秒数(默认 7200) |
| `OFFICECLI_NO_AUTO_RESIDENT` | 置 1 禁止自动拉起常驻(显式 `open` 仍可用) |
| `OFFICECLI_WATCH_ALLOWED_HOSTS` | watch 预览服务允许的来源主机(默认本机) |
| `OFFICECLI_SKIP_UPDATE` | 置 1 跳过非阻塞更新检查 |
| `OFFICECLI_BATCH_ALLOW_STDIN_REDIRECT` | 允许 batch 从重定向 stdin 读命令(默认关,防脚本误用) |
| `OFFICECLI_LEDGER_API` | 账本(issue/artifact)基址覆盖(测试/灰度) |
| `OFFICECLI_LEDGER_KEY_FILE` | Ed25519 私钥密档路径(缺省 `~/.officecli/ledger/officecli-ed25519.key`;或用 `OFFICECLI_LEDGER_KEY` 直设种子) |
| `OFFICECLI_LOCAL_BINARY` | 安装脚本用本地二进制(离线装机) |
| `OFFICECLI_MMDC` | mermaid CLI(`mmdc`)路径覆盖 |

配置文件:`~/.officecli/config.json`(更新器状态,自动维护)。密钥纪律:产品零内嵌密钥,更新与 issue 上报只走公开端点。

## 使用方法

```bash
officecli create report.docx
officecli add report.docx /body --type paragraph --prop text="Summary" --prop style=Heading1
officecli set data.xlsx '/Sheet1/A1' --prop value=Name --prop bold=true
officecli get slides.pptx '/slide[1]' --depth 1 --json
officecli query report.docx 'paragraph[style!=Normal]'
officecli view deck.pptx html          # 渲染快照(agent 先看再修)
officecli validate report.docx && officecli close report.docx
```

发现缺陷?上仓级公共账本开单:`officecli issue new "<标题>" --kind bug --acceptance "<验收>"`(真源 ledger.ohmygh.com,REQ-063;产物沉淀 `officecli artifact publish`)。
Agent 紧凑说明书:`officecli --llms`(机器形 `--llms --json`)。完整能力参考:`officecli help`。
