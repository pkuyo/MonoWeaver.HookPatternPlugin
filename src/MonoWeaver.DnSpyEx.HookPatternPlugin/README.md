# MonoWeaver.DnSpyEx.HookPatternPlugin

在 dnSpyEx 的反编译代码中右键当前语句，选择 **Generate MonoWeaver HookPattern...**。
也可以把光标放在目标语句内或先选中代码，再按 **Ctrl+Alt+H**。
插件使用 dnSpyEx 提供的 `MethodDebugInfo`/`ILSpan` 定位 CIL，再由独立的
`MonoWeaver.PatternGeneration.dll` 生成并回匹配验证 lambda Pattern。

插件引用 `MonoWeaver` 0.1.1，并自带与其程序集引用一致的 Mono.Cecil 0.11.2；
不再使用 `MonoWeaver.Cecil10`，也不需要修改 `dnSpy.exe.config`。

## DLL 拆分

- `MonoWeaver.DnSpyEx.HookPatternPlugin.x.dll`：只放 dnSpyEx 菜单、语句定位和 WPF 窗口。
- `MonoWeaver.PatternGeneration.dll`：只放 IL → lambda Pattern 的反向生成逻辑，可被其他宿主复用。
- `MonoWeaver.dll`：运行时匹配与改写核心，不引用 dnSpyEx。

这样 dnSpyEx API 的版本变化不会污染运行时库，未来写 ILSpy 前端也只需替换宿主适配 DLL。

## 当前范围

- 支持 Value、Effect、普通及 `&&`/`||` 短路 Condition。
- 支持 Before/After；Condition 自动隐藏 After。
- 只读取已经保存到磁盘的模块；dnSpy 中未保存的修改不会被猜测使用。
- 私有成员会生成 lambda，但会提示需要 publicized reference assembly。
- 第一版面向 dnSpyEx 的 .NET Framework 4.8 构建。

## 构建

准备 dnSpyEx .NET Framework 版，将安装目录传给 `DnSpyDir`：

```powershell
dotnet build src/MonoWeaver.DnSpyEx.HookPatternPlugin/MonoWeaver.DnSpyEx.HookPatternPlugin.csproj `
  -c Release `
  -p:DnSpyDir=C:\Tools\dnSpy
```

把以下文件复制到 dnSpyEx 的独立扩展目录，例如
`bin/Extensions/MonoWeaver.PatternStudio/`：

- `MonoWeaver.DnSpyEx.HookPatternPlugin.x.dll`
- `MonoWeaver.PatternGeneration.dll`
- `MonoWeaver.dll`
- `Mono.Cecil.dll`
- `zh-CN/MonoWeaver.DnSpyEx.HookPatternPlugin.x.resources.dll`（简体中文界面）

插件界面语言跟随 dnSpyEx 的当前 UI culture；缺少对应翻译时回退到英文。

dnSpyEx 扩展 API 属于 GPLv3 项目；分发插件二进制前应单独确认适用的许可证义务。
