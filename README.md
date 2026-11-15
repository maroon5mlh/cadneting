# CAD 排样插件（钣金激光切割 · 块级共边排版）

AutoCAD 插件：选中一批闭合图形块 → 提取轮廓 → 归并计数 → 排样 → 按板平铺回写。

**全程不炸开块**：只读遍历块定义得到轮廓，排完只改 `BlockReference` 的位置和旋转。

## 当前能力

| 命令 | 作用 |
|---|---|
| `NESTPANEL` | 打开停靠面板（参数 + 结果） |
| `NESTAUTO` | 面板按钮实际执行的命令：选块 → 提取 → 排样 → 摆放 |
| `NESTEXTRACT` | 只提取，导出 `nestjob_*.json`（用于核对品种和件数） |
| `NESTRUN` | 读任务 JSON → 排样 → 摆放 → 写 `.result.json` |
| `NESTAPPLY` | 只读结果 JSON → 摆放（重放已有方案） |
| `NESTDIAG` | 自检：加载的是哪个 dll、依赖是否齐、面板是否创建成功 |

**不包含**（按确认的需求，明确不做）：割缝补偿、引线、穿孔点、微连/桥接、刀路生成。

## 排样引擎

`Nesting.Core/Solvers/` 里是自研引擎，不依赖任何外部进程或第三方库。

**算法：MaxRects 自由矩形装箱 + 多启发式择优。**

选择理由：激光切割没有通切约束，MaxRects 比 Guillotine 排得更紧（Guillotine 要求每刀贯穿整板）。
零件按**包围盒**参与碰撞判定——轮廓必然 ⊆ 包围盒，所以结果**永远不会重叠**，
只是轮廓凹陷处会浪费一点空间。

判定依据是**包围盒填充率**（`NetArea / 包围盒面积`）：

- ≥ 97%：基本是矩形，包围盒排样损失 < 1%，毫秒级出结果
- < 97%：L 形/U 形，包围盒会明显浪费，引擎会在结果 `Warnings` 里点名这些品种

实测你那批封板件的填充率是 **99.75% / 99.55%**（带角部台阶的矩形），属于第一类。

求解流程：4 种排序（面积/长边/周长/高度降序）× 3 种插入启发式（最贴短边/最贴面积/左下优先），
共 12 次尝试，取「张数最少 → 板材总面积最小」的方案。

## 目录结构

```
cad-nesting/
├── build.cmd / build.ps1              编译脚本（自动识别 CAD 路径，输出带时间戳绕开 dll 锁）
├── CadNesting.bundle/                 插件清单（自动加载用）
├── src/
│   ├── Nesting.Core/                  纯 .NET 类库，无 AutoCAD 依赖，可单元测试
│   │   ├── Models.cs                  数据模型与 JSON 协议
│   │   ├── Geometry.cs                归一化、几何指纹、凸度圆弧离散
│   │   ├── RotationPolicy.cs          旋转三层判定（自身/全局/关键字白名单）
│   │   ├── PartGrouper.cs             品种归并
│   │   └── Solvers/
│   │       ├── RectPacker.cs          MaxRects 装箱器（自由矩形 + 剪枝 + 留边）
│   │       └── NestEngine.cs          多板、多规格、多启发式求解
│   └── Nesting.AutoCAD/               AutoCAD 插件
│       ├── Commands.cs                NESTPANEL / NESTAUTO / NESTEXTRACT / NESTRUN / NESTAPPLY
│       ├── Extraction/
│       │   ├── CurveFlattener.cs      曲线离散（优先用 AutoCAD 原生圆弧几何）
│       │   ├── TextCompanionFinder.cs 块与块外标注文字关联（几何推断）
│       │   └── BlockOutlineExtractor.cs  块递归遍历、坐标变换、外轮廓/内孔判定
│       └── UI/
│           ├── NestPalette.cs         PaletteSet 容器
│           └── NestPaletteControl.cs  面板界面
└── tests/SmokeTest/                   不依赖 AutoCAD 的冒烟测试
```

编译产物（`bin/` `obj/` `build/` `dist/`、`CadNesting.bundle/Contents/Windows/*.dll`）和本机路径配置
（`AcadDir.local.props`）都不进仓库，见 `.gitignore`。

## 环境（已实测确认）

| 项 | 值 |
|---|---|
| AutoCAD | 2027，`R26.0.60.0` |
| 运行时 | .NET 10（`acdbmgd.runtimeconfig.json` 中 `tfm=net10.0`） |
| 目标框架 | `net10.0-windows` |
| 平台 | x64（程序集 `Amd64`） |
| 引用程序集 | `acmgd` / `acdbmgd` / `accoremgd` 均 `26.0.0.0` |

> AutoCAD 2025 及以后都是 .NET 8+，不能再用 `net48`。

## 编译

```cmd
git clone git@github.com:maroon5mlh/cadneting.git
cd cadneting

build.cmd
```

`build.cmd` 会自动从注册表识别 AutoCAD 安装目录（也支持 `ACAD_DIR` 环境变量），
输出到 `build\<时间戳>\`，打印需要 NETLOAD 的完整路径，并把两个 dll 同步进 `CadNesting.bundle\Contents\Windows\`。

**为什么要带时间戳**：NETLOAD 过的 dll 会被 AutoCAD 进程锁住直到关闭 CAD，
直接覆盖 `dist\` 会报 `MSB3021 文件被占用`。每次输出到新目录就不用等解锁。

自动识别失败（多版本共存、绿色安装、装在其他盘）时，任选一种方式指定：

```cmd
:: 方式一：命令行传参
build.cmd "C:\Program Files\Autodesk\AutoCAD 2026"

:: 方式二：本机配置文件（已被 .gitignore 排除，不会进仓库）
::   新建 src\Nesting.AutoCAD\AcadDir.local.props，内容：
::   <Project><PropertyGroup><AcadDir>D:\CAD2017\AutoCAD 2027</AcadDir></PropertyGroup></Project>

:: 方式三：环境变量
set ACAD_DIR=D:\CAD2017\AutoCAD 2027 && build.cmd

:: 方式四：直接用 dotnet（需方式二或三已配置）
dotnet build src/Nesting.AutoCAD/Nesting.AutoCAD.csproj -c Release -p:AcadDir="你的AutoCAD目录"
```

找不到 `acmgd.dll` 时构建会直接报一句人话，而不是抛一串 HintPath 警告。

## 冒烟测试（不需要 AutoCAD）

```bash
dotnet run --project tests/SmokeTest -c Release
dotnet run --project tests/SmokeTest -c Release -- debug   # 打印重叠诊断
```

覆盖：
- 凸度圆弧采样精度（弦高控制）、正负 bulge 方向
- 品种归并计数、JSON 往返、中文块名编码
- **实图排样**：自动读取桌面上最新的 `nestjob_*.json` 跑一遍，张数按面积下限校验
- **压测**：428 件合零件，校验全部落位、板内不越界、**零件之间零重叠**、**避开留边**
- **留边**：四边都守（只查左边会漏掉"贴右边切不出来"）
- **关键字旋转**：全局关掉旋转时，命中关键字的件仍必须转 90°；未命中的必须报错而不是悄悄转
- **文字句柄**：按实例传递、空串不产生句柄、不串位

## 加载

```cmd
NETLOAD  →  build\<时间戳>\Nesting.AutoCAD.dll
NESTPANEL
```

`Nesting.AutoCAD.dll` 和 `Nesting.Core.dll` 必须在同一目录。

## 使用

1. `NESTPANEL` 打开面板
2. 勾选板材规格，填三个参数：
   - **间距**：零件之间净距，默认 0.2mm（共边场景留一点点防烧穿）
   - **留边**：零件到板四边的距离，默认 **5mm**。四边等距，因为板边不整齐、
     边缘吸附和夹具都需要余量；只留左下两边会出现"贴着上/右边的件切不出来"
   - **强制可转**：块名含任一关键字即允许 90° 旋转，**不受「允许旋转 90°」全局开关影响**。
     默认 `顶包边、底框、中横框`，用顿号/逗号/空格分隔
3. 「文字跟随块」默认勾上：排样时块旁边的标注文字跟着一起移动、一起转
4. 点「选择图形并排样」，框选块参照
5. 结果表格显示每张板的件数和利用率；模型空间里按板平铺（板间距 300mm）

## 三处行为细节（容易踩的坑都在这）

**留边不是排完再加偏移。** 可用区域的自由矩形从 `(margin, margin)` 起、尺寸减 `2*margin`，
所以引擎输出的 `X/Y` 天然已经含留边。事后加偏移会在板材尺寸、利用率、越界判断三处各埋一个坑。

**旋转是三层判定，顺序不能反**：

```
零件自身允许(纹向/折弯硬约束)  →  全局开关  →  关键字白名单
                                      ↑              ↑
                                  批量关掉      命中即放行，覆盖全局
```

关键字用「包含」匹配，所以 `顶包边-2611060` 能命中 `顶包边`。
**如果一条都没命中，而全局开关是关的，引擎会在 `Warnings` 里明确报出来** ——
否则表现是"排得通、但比预期多一两张板"，几乎不可能联想到关键字写错了。

**文字跟随靠几何推断，不靠链接。** XK 命令把文字写成模型空间里独立的 `DBText`/`MTEXT`，
位置取块**包围盒中心**，和块之间没有任何分组、反应器或扩展数据。
所以插件按"文字插入点落在块的包围盒内 → 归给中心最近的那个块"来关联，
一个块可以有多条文字（名称 + 数量），全部跟随。

关联结果会在命令行打印：`跟随文字：关联 26 条，覆盖 26 件`。
**如果这个数字是 0，说明文字不在块的包围盒内**（例如放在了块外面一段距离），
把文字 `MOVE` 进块范围再跑，或者告诉我实际偏移量，我改成按距离匹配。

文字跟着块转 90° 后如果角度落在 (90°, 270°)，会被自动减 180° 纠正回正方向
——否则块转 90° 时原本竖排的文字会变成倒字，打印出来是反的。

## 排障

**面板不显示** → 先跑 `NESTDIAG`，看程序集路径是不是当前这次编译的产物、`Nesting.Core.dll` 是否在同一目录。
若面板被折叠成了侧边的竖排标签，点一下那个标签即可展开。

**必须重启 AutoCAD 才能换新 dll**。NETLOAD 过的程序集在 AutoCAD 进程内无法卸载，
同名 dll 再次 NETLOAD 会沿用旧的那一份，表现为「改了代码但行为没变」。这也是构建输出带时间戳的原因。

**`eKeyNotFound`**：板框图层 `NEST_SHEET` 不存在。已在代码里按需创建图层，
不再依赖图纸预先存在该图层（早期版本在这里抛异常会导致整个事务回滚，一件都摆不上）。

**`eOnLockedLayer` / 摆放失败 N 件**：块所在图层被锁定。命令会跳过失败的块并继续，
解锁对应图层后重跑即可。

## 面板与 CAD 线程的关系（重要）

面板是**模态外**的，从按钮事件里直接调用 `Editor` / `Database` 会抛 `eInvalidContext` 或随机崩溃。

做法：按钮只发一条命令

```csharp
doc.SendStringToExecute("NESTAUTO ", true, false, false);
```

执行权回到文档上下文后再操作数据库。这条链路上没有任何跨线程调用。

## JSON 协议

### 输入（NestJob）

```json
{
  "Unit": "mm",
  "BatchId": "B20261115_153000",
  "Sheets": [
    { "Name": "1220x2440", "Width": 1220, "Height": 2440, "MaxCount": 2147483647, "Price": 0 }
  ],
  "Parts": [
    {
      "Id": "P001",
      "BlockName": "排版-下前封板0",
      "Outer": { "Points": [ { "X": 0, "Y": 0 }, { "X": 1200, "Y": 0 } ] },
      "Holes": [],
      "NetArea": 480000,
      "Width": 1200,
      "Height": 400,
      "AllowRotate90": true,
      "Quantity": 12,
      "Handles": ["2A1", "2A2"],
      "TextHandles": ["3B4,3B5", "3B6"]
    }
  ],
  "Settings": {
    "SpacingMm": 0.2,
    "MarginMm": 5,
    "AllowRotate90": true,
    "RotateKeywords": ["顶包边", "底框", "中横框"],
    "TimeLimitSec": 60,
    "Objective": "sheets",
    "FlattenTolerance": 0.1
  }
}
```

- `Outer.Points` 已归一化到「包围盒左下角为原点」，首尾点不重复
- `Handles` 与 `Quantity` 一一对应，是回写时的定位依据
- `TextHandles` 与 `Handles` **严格同序**，每项可能含多个句柄（逗号分隔），空串表示该件没有文字
- `AllowRotate90` 可按品种单独关闭（有纹理/折弯方向要求的件）
- `MarginMm` 留边，`RotateKeywords` 强制可转关键字

### 输出（NestResult）

```json
{
  "Sheets": [
    {
      "Index": 0, "SheetName": "1220x2440", "Width": 1220, "Height": 2440,
      "UsedArea": 2400000, "Utilization": 0.817,
      "Items": [
        { "PartTypeId": "P001", "Handle": "2A1", "SheetIndex": 0,
          "X": 5, "Y": 5, "RotationDeg": 90,
          "TextHandles": ["3B4", "3B5"] }
      ]
    }
  ],
  "TotalUtilization": 0.817,
  "ElapsedMs": 11,
  "Warnings": []
}
```

`X/Y` 是**旋转后**零件包围盒左下角相对板材左下角的位置，已包含 `MarginMm` 留边。

## 已知限制

1. 一个块里若含多个独立零件，只取最大轮廓并给出警告
2. `Polyline2d` / `Polyline3d` / `Region` / `Hatch` 不参与轮廓提取
3. 轮廓离散精度默认 0.1mm（按**弦高**控制，不是弦长）
4. 归并指纹按 0.01mm 取整，图纸有微小差异的「同款零件」会被当成不同品种
5. 同一零件以 90° 插入会被识别成两个品种（指纹基于世界坐标轮廓），不影响排样正确性
6. 排样**会修改块位置**，建议先备份图纸；不满意直接 `U` 撤销
7. 包围盒排样对填充率 < 97% 的异形件会浪费，目前只警告不优化
8. 多规格混用时按「张数最少 → 板材总面积最小」择优，不做成本最优
9. **文字关联靠几何推断**：文字必须落在块的包围盒内。放在块外一段距离的文字不会被关联，
   会在警告里报"有 N 个文字没落在任何被选中的块里"
10. 文字如果是**在块定义内部**的，不需要关联（它本身随块移动），不占 `TextHandles`

## 下一步

- [ ] 异形件接入 NFP 引擎（填充率 < 97% 的品种）
- [ ] 文字关联支持"块外偏移"场景（按最近块 + 距离阈值匹配）
- [ ] 余料库：切剩的边角料编号回库，下批优先用
- [ ] 利用率报表导出（按品种、按板材，Excel/CSV）
- [ ] 排样方案对比（同一批料跑多个方案，选最优）
