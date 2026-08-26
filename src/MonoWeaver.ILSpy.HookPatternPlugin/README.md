# MonoWeaver.ILSpy.HookPatternPlugin

面向 ILSpy 11.0（Avalonia、.NET 10）的独立插件。它只复用
`MonoWeaver.PatternGeneration`，不引用 dnSpyEx 插件项目。

## 构建

```powershell
dotnet build src/MonoWeaver.ILSpy.HookPatternPlugin/MonoWeaver.ILSpy.HookPatternPlugin.csproj `
  -c Release `
  -p:ILSpyDir=C:\Tools\ILSpy
```

把以下文件放到 `ILSpy.exe` 同一目录：

- `MonoWeaver.ILSpy.HookPatternPlugin.Plugin.dll`
- `MonoWeaver.PatternGeneration.dll`
- `MonoWeaver.dll`
- `zh-CN/MonoWeaver.ILSpy.HookPatternPlugin.Plugin.resources.dll`

ILSpy 只扫描程序目录下的 `*.Plugin.dll`。ILSpy 自带 Mono.Cecil 0.11.x，
不要随插件重复分发 Mono.Cecil。

## 使用

在 C# 反编译视图中选择代码后右键 **Generate MonoWeaver HookPattern...**，
或将光标放在目标语句内按 `Ctrl+Alt+H`。
