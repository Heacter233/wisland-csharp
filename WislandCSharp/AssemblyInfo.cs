using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// 单元测试需要访问 internal 的下载器/模型校验等纯逻辑类型。
// 只对测试程序集开放，不影响插件隔离（插件仍只能看到 public 契约）。
[assembly: InternalsVisibleTo("Wisland.Tests")]
