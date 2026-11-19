# 液位管理系统 r24

当前上位机源码与交付版本为 **r24（V0.3.0，构建号 20261118-r24，数据库结构 18）**。2026-11-19 更新包含局域网 IP 访问、网页罐图显示和卸载后沿用原业务数据库重装的修正。单罐详情和趋势曲线菜单的实时曲线显示最近 1 小时。

安装包、模拟器、授权工具及本机启动入口的位置见[文件导航](00_文件导航.md)。安装程序和说明书在本地交付目录单独保管。

128罐、默认60秒历史、默认12个月与SQLite为容量基线。原协议、客户原件、原型及其他项目源代码保持原样。

## 规划与恢复入口

- [整体开发方案](docs/整体开发方案_V1.2_20261027.md)
- [分阶段任务与验收清单](docs/分阶段任务与验收清单_V1.3_20261028.md)
- [项目进度](docs/project_memory/progress.md)
- [本轮基础工程验证](docs/P1基础工程实施与验证_20261027.md)

## 软件授权

授权到期后保留实时监控与必要处置，限制新任务、常规控制和历史查询导出。到期规则见[授权设计记录](docs/project_memory/decisions/ADR-004-到期授权与最后阶段英文支持.md)。

## 内部体验

本开发机双击“启动工程版.cmd”，默认打开http://127.0.0.1:8780。首次在采集电脑本机建立自己的管理员，然后登录。数据保存在本工程data/engineering，与原型、上个温度项目和网关数据库分开。关闭浏览器不会关闭后台；双击“停止工程版.cmd”停止后台。若8780被占用，可用tools/start-engineering.ps1 -Port 8781。

首次启动后由管理员配置储罐和通道。客户通过 r24 安装向导安装，安装包内含 .NET 运行时、桌面客户端及 Microsoft Edge WebView2 离线安装程序。

## 构建与验证

采用C#/.NET10/ASP.NETCore、EFCore及Microsoft.Data.Sqlite/SQLite和Vue3/TypeScript。开发机SDK已校验官方SHA512并仅存项目.build-tools，前后端依赖均固定并保留锁文件；不改上个项目或网关源代码。

```powershell
./tools/build.ps1
# 新开发机先配置.NET10 SDK、Node和pnpm，再恢复锁定依赖
./tools/build.ps1 -Restore
node ./tests/engineering-ui-check.cjs
./tools/estimate-capacity.ps1 -Tanks 128 -IntervalSeconds 60 -Days 365
```

构建输出在output/engineering。浏览器验证启动独立临时数据库及回环端口，测试账号只在隔离目录，不写真实data/engineering。受限沙箱可能阻止VSTest及测试服务器回环连接，须在允许本机测试进程通信的环境运行；不要把环境连接超时误判为测试通过。

容量估算器根据罐数、归档间隔和天数计算常规历史条数。容量验证过程和各版本检查结果可在[项目进度](docs/project_memory/progress.md)中查阅。

## 目录

src为正式分层后台；web为Vue前端；tests为逻辑/SQLite/浏览器验证；tools为构建与启动；docs为方案、任务和实施依据。archive/go-exploration-20261027保存未验证Go探索稿及散列，不用于正式构建。output、data和.build-tools均不作为客户源代码或业务数据随意分发。

现行版本的改动见 [r24 设计记录](docs/project_memory/decisions/ADR-021-r24客户反馈与带故障操作.md)。后续修正及其验证结果见[项目进度](docs/project_memory/progress.md)顶部。
