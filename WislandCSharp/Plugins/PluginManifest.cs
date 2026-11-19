using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisland.Plugins;

/// <summary>
/// 插件清单（plugins/&lt;id&gt;/plugin.json）。
///
/// 这是宿主识别、校验、展示插件的唯一依据；缺失或字段非法时插件不会被加载。
/// 字段与约束详见 docs/plugins.md。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>插件唯一标识：小写字母/数字/点/下划线/连字符，2~64 字符，全局唯一。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名（设置页标题）。</summary>
    public string Name { get; init; } = "";

    /// <summary>插件版本，建议语义化版本（1.2.3）。</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>作者。</summary>
    public string Author { get; init; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>入口类型全名（命名空间.类名），必须实现 IWislandPlugin。</summary>
    public string Entry { get; init; } = "";

    /// <summary>程序集文件名（相对插件目录）。</summary>
    public string Assembly { get; init; } = "";

    /// <summary>要求的最低宿主版本；为空表示不校验。</summary>
    public string MinHostVersion { get; init; } = "";

    /// <summary>声明使用的权限（见 PluginPermissions）。未声明的权限调用会被宿主拒绝。</summary>
    public List<string> Permissions { get; init; } = new();

    /// <summary>主页/仓库地址（可选）。</summary>
    public string Homepage { get; init; } = "";

    /// <summary>插件所在目录（由加载器填充，不写在 json 里）。</summary>
    [JsonIgnore]
    public string Directory { get; set; } = "";

    /// <summary>清单文件路径。</summary>
    [JsonIgnore]
    public string ManifestPath { get; set; } = "";

    public bool Has(string permission) =>
        Permissions.Exists(p => string.Equals(p, permission, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>读取并做基础校验。失败时返回 null 并给出原因。</summary>
    public static PluginManifest? TryLoad(string manifestPath, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(manifestPath))
            {
                error = "找不到 plugin.json";
                return null;
            }

            var json = File.ReadAllText(manifestPath);
            var m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (m == null)
            {
                error = "plugin.json 解析结果为空";
                return null;
            }

            m.ManifestPath = manifestPath;
            m.Directory = Path.GetDirectoryName(manifestPath) ?? "";

            var invalid = m.Validate();
            if (invalid != null)
            {
                error = invalid;
                return null;
            }
            return m;
        }
        catch (Exception ex)
        {
            error = $"plugin.json 解析失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>校验字段合法性；返回 null 表示通过。</summary>
    public string? Validate()
    {
        if (!IsValidId(Id))
            return $"id 非法（要求小写字母/数字/点/下划线/连字符，2~64 字符）：\"{Id}\"";
        if (string.IsNullOrWhiteSpace(Name))
            return "缺少 name";
        if (string.IsNullOrWhiteSpace(Entry))
            return "缺少 entry（入口类型全名）";
        if (string.IsNullOrWhiteSpace(Assembly))
            return "缺少 assembly（程序集文件名）";
        if (Assembly.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            return "assembly 只能是文件名，不能包含路径";
        if (!File.Exists(Path.Combine(Directory, Assembly)))
            return $"程序集不存在：{Assembly}";

        foreach (var p in Permissions)
        {
            if (!PluginPermissions.IsKnown(p))
                return $"未知权限：{p}（可用权限见 docs/plugins.md）";
        }
        return null;
    }

    /// <summary>id 规则：小写字母/数字开头，允许 . _ -，2~64 字符。</summary>
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length < 2 || id.Length > 64) return false;
        if (!char.IsLetterOrDigit(id[0])) return false;
        if (!char.IsLower(id[0]) && !char.IsDigit(id[0])) return false;

        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
