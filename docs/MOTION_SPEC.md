# AstraCat 动效设计与工程规范 (Motion Specification)

本文档定义 AstraCat 客户端的动画设计哲学、多维空间动效法则、物理缓动曲线数学模型、交互时序契约以及工程层面的防卡死与防闪烁规范。

---

## 1. 核心设计哲学与架构原理

AstraCat 采用**增量物理动画调度器（Additive Animation Scheduler）**，彻底告别传统的绝对插值替换，确保高频打断与快速交互时的平滑连续。

### ① 增量 Delta 叠加（Additive Delta Tracking）
- 动画轨道仅施加**增量分量（$\Delta$）**，而非直接覆盖属性的绝对值。
- 当同一属性在运动中被打断或施加新动作时，新旧轨道在当前物理状态上自然叠加合成，**绝不发生瞬间归零或突变跳帧（Zero Snapping）**。

### ② 多维空间动效矩阵（Multi-Dimensional Spatial Matrix）
动效方向与屏幕物理层级严格对齐，形成向心聚拢的层次感：
- **左侧导航与列表项（Left Rail / List Items）**：沿 **X 轴横向展开（从左往右 $-25\text{px} \to 0\text{px}$）**，配合递减阶梯延迟，呈现如扇面展开的灵动感。
- **右侧主内容卡片（Right Main Content / Cards）**：沿 **Y 轴垂直落位（自上而下 $-16\text{px} \to 0\text{px}$）**，配合两段式下落与微弹吸附。
- **全局胶囊指示条（Indicator）**：沿主轴以高阶阻尼曲线（`FluentOutStrong`）平滑滑移。

### ③ 动静分离策略（Dynamic & Static Separation）
- **展示型页面（主页、任务概览、模型展示）**：启用舒缓轻盈的双轨微弹落位动画（320ms / 420ms）。
- **高密度配置型页面（翻译模型服务商、全局设置）**：采用**即时静态挂载（Instant Swap）**，消除繁复动画对高频配置操作的干扰，同时配合强力全景恢复器保证卡片 100% 完整显示。

### ④ 状态机自愈保障（State Auto-Healing & Anti-Ghosting）
- 引入**导航纪元追踪（`_navigationEpoch`）**，确保任意高频连击与打断均精准收敛于最新目标。
- 每次切换与归位时通过 `RestorePage` / `RestorePageItems` 强制无条件校准透明度（`Opacity = 1`）与交互（`IsHitTestVisible = true`），杜绝“幽灵隐形卡片”。

---

## 2. 物理缓动曲线与数学模型 (Easing Curves)

调度器内置基于物理特性的高阶缓动函数，定义域均严格截断在 $[0, 1]$，输出 Delta 采用 7 位浮点精度计算以防止累积误差：

| 曲线名称 | 数学公式 / 算法模型 | 典型应用场景 |
| :--- | :--- | :--- |
| **`Linear`** | $f(t) = t$ | 基础颜色过渡、短时线性淡出 |
| **`FluentOutWeak`** | $f(t) = 1 - (1 - t)^2$ | 快速透明度淡入（100ms ~ 160ms） |
| **`FluentOut`** | $f(t) = 1 - (1 - t)^3$ | 第一阶段快速起跑推进（200ms ~ 320ms） |
| **`FluentOutStrong`** | $f(t) = 1 - (1 - t)^4$ | 侧边栏指示条平滑滑移、窗口入场位移 |
| **`FluentOutExtraStrong`** | $f(t) = 1 - (1 - t)^5$ | 卡片箭头旋转、按钮按下极速响应 |
| **`BackOutWeak`** | $f(t) = 1 - (1 - t)^2 \cdot \cos(1.5\pi t)$ | 左侧列表项微弹性吸附（300ms） |
| **`BackOut`** | $f(t) = 1 - (1 - t)^{1.5} \cdot \cos(1.5\pi t)$ | 右侧卡片自然弹性落位（350ms ~ 420ms） |
| **`DrawerCurve`** | 三次贝塞尔曲线：$\text{Cubic-Bezier}(0.16, 1, 0.3, 1)$ | 抽屉面板展开与收起 |

---

## 3. 详细交互时序契约 (Timing Specifications)

### ① 页面与窗口级过渡 (Page & Window Transitions)

| 交互场景 | 目标元素 | 轨道拆解 | 时长 | 延迟 | 曲线 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **主窗口加载** | `WindowChrome` | 透明度淡入<br>Y 轴平移 | 220ms<br>260ms | 30ms<br>30ms | `Linear`<br>`FluentOut` |
| **主窗口退出** | `WindowChrome` | 透明度淡出<br>Y 轴下沉<br>微缩放<br>微旋转 | 140ms<br>180ms<br>180ms<br>180ms | 40ms<br>0ms<br>0ms<br>0ms | `FluentOutWeak`<br>`FluentOutWeak`<br>`Linear`<br>`FluentInOutWeak` |
| **主页 / 任务 / 模型入场** | 顶层内容卡片组 | **轨道 1**：透明度淡入<br>**轨道 2**：Y 轴初段推进 ($+5\text{px}$)<br>**轨道 3**：Y 轴末段落位 ($+11\text{px}$) | 160ms<br>320ms<br>420ms | 阶梯递增：<br>$\min(\text{idx} \times 30, 150)$ | `FluentOutWeak`<br>`FluentOut`<br>`BackOut` |
| **页面退场** | 顶层内容卡片组 | 透明度快速淡出<br>Y 轴微上浮 ($-6\text{px}$) | 80ms<br>80ms | 阶梯递增：<br>$\min(\text{idx} \times 12, 36)$ | `Linear`<br>`Linear` |
| **子列表项入场 (Left Rail)** | 侧边栏/服务商条目 | **轨道 1**：透明度淡入<br>**轨道 2**：X 轴初段前推 ($+5\text{px}$)<br>**轨道 3**：X 轴末段回弹 ($+20\text{px}$) | 100ms<br>200ms<br>300ms | 递减阶梯：<br>$\Delta += \max(15-\text{idx}, 7) \times 2$ | `FluentOutWeak`<br>`FluentOut`<br>`BackOutWeak` |
| **导航指示条滑移** | `SidebarNavIndicator` | Y 轴位移差量增量滑移 | 280ms | 0ms | `FluentOutStrong` |

---

### ② 控件微交互 (Micro-Interactions)

| 控件类型 | 交互状态 | 动画行为 | 时长 | 曲线 |
| :--- | :--- | :--- | :--- | :--- |
| **标准按钮 (Button)** | 按下 (PointerDown)<br>释放 (PointerUp)<br>移出 (PointerExit) | 缩放至 $0.955$<br>平滑回弹至 $1.0$<br>平滑回弹至 $1.0$ | 80ms<br>250ms<br>250ms | `FluentOutExtraStrong`<br>`FluentOut`<br>`FluentOut` |
| **窗口图标按钮 (IconButton)** | 按下 (PointerDown)<br>释放 (PointerUp) | 深度内缩至 $0.80$<br>超调回弹至 $1.05$ 后归位至 $1.0$ | 400ms<br>180ms + 250ms | `FluentOutStrong`<br>`FluentOutStrong` |
| **导航按钮 (NavButton)** | 悬停 (Hover)<br>按下 (Press)<br>离开 (Leave) | 背景高亮淡入 + 背景层微缩放 ($1.0$)<br>整体微内凹 ($0.98$)<br>背景淡出 + 背景层收缩至 $0.75$ | 120ms<br>108ms<br>180ms | `FluentOut`<br>`FluentOut`<br>`FluentOut` |
| **模型列表行 (ModelRow)** | 悬停 (Hover)<br>按下 (Press)<br>离开 (Leave) | 背板柔和变色 + 整体轻微浮起 ($1.0$)<br>整体按压内缩 ($0.98$)<br>背板淡出 + 行尺寸平滑归位 ($1.0$) | 120ms<br>108ms<br>180ms + 540ms | `FluentOut`<br>`FluentOut`<br>`FluentOut` |
| **折叠卡片 (Accordion)** | 展开 / 折叠 | 内容区高度物理展开 ($0 \leftrightarrow H$)<br>Chevron 箭头平滑旋转 ($0^\circ \leftrightarrow 180^\circ$) | 自适应 (180ms ~ 320ms)<br>250ms | `FluentOut`<br>`FluentOutExtraStrong` |

---

## 4. 工程防卡死与防闪烁准则 (Engineering Guidelines)

为确保在任何硬件环境与极端连点操作下的高可靠性，所有动效代码必须严格遵守以下工程红线：

1. **禁止在 UI 线程同步等待耗时操作**：
   - 导航处理、标签切换或动画回调中，**严禁使用 `await` 执行网络请求、远程模型检测或磁盘 IO**。
   - 所有网络或耗时任务必须以后台非阻塞任务（`_ = TaskAsync()`）运行，数据返回后再通过 Dispatcher 安全更新 UI。
2. **严禁在取消分支中回滚历史页面可见性**：
   - 动画被中途打断（`OperationCanceledException`）时，**绝对禁止将旧页面设回 `IsVisible = true`**，避免视觉闪烁。
3. **强制页面与卡片自愈（Auto-Healing）**：
   - 无论页面是通过动画进入、即时挂载还是被快速连击打断，结束时必须调用 `RestorePage` / `RestorePageItems`，确保目标页面所有卡片的 `Opacity`、`IsHitTestVisible` 与 Transform 矩阵处于可用状态。
4. **按钮缩放状态脱钩集合锁**：
   - 按钮释放与移出动画不可受限于 `_pressedButtons` 是否存在，凡是离开按压状态，均必须执行回弹至 1.0 的安全归位。
5. **帧回调时间差截断（Delta Clamping）**：
   - 帧回调时间跨度必须截断在 $[0.1\text{ms}, 100\text{ms}]$ 之间，防止系统休眠唤醒或瞬间卡顿导致下一帧时间差暴增而发生画面飞出屏幕。

