using System.Text;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Templates;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>
/// 模板非法时的**可定位性**（G4 前半：「非法模板可定位」）。
/// 每一项都断言三件套：错误码 + 模板名 + 字段。缺任何一个，用户都得从头读整个 JSON。
/// </summary>
public static class TemplateValidationTests
{
    /// <summary>最小可用模板：改一处即可造出各类坏配置，避免每例手写整份配置时漏掉必填项。</summary>
    private const string Valid = """
        {
          "T": {
            "structure": { "src": ["App"] },
            "descriptions": { "src": "源码" },
            "readme_quickstart": "dotnet run"
          }
        }
        """;

    public static void Run()
    {
        Check.Section("模板校验定位（G4）");

        StructureErrors();
        RequiredFieldErrors();
        ModuleErrors();
        OtherFieldErrors();
        ConfigLevelErrors();
        WarningsAreNotErrors();
    }

    private static void StructureErrors()
    {
        ExpectField(
            "structure 非对象",
            Valid.Replace("\"structure\": { \"src\": [\"App\"] }", "\"structure\": \"src\""),
            "structure");

        ExpectField(
            "structure 含 ..",
            Valid.Replace("\"src\": [\"App\"]", "\"..\": [\"App\"]"),
            "structure");

        ExpectField(
            "structure 含盘符",
            Valid.Replace("\"src\": [\"App\"]", "\"src\": [\"C:/Windows\"]"),
            "structure");

        ExpectField(
            "structure 为空对象",
            Valid.Replace("\"structure\": { \"src\": [\"App\"] }", "\"structure\": {}"),
            "structure");

        ExpectTooLarge("structure 目录数超限");
    }

    private static void RequiredFieldErrors()
    {
        ExpectField(
            "缺 descriptions",
            Valid.Replace("\"descriptions\": { \"src\": \"源码\" },", string.Empty),
            "descriptions");

        ExpectField(
            "descriptions 值非字符串",
            Valid.Replace("{ \"src\": \"源码\" }", "{ \"src\": 42 }"),
            "descriptions「src」");

        // 末尾属性删掉后留下裸逗号，配置解析允许尾逗号——正好顺带验证这一点
        ExpectField(
            "缺 readme_quickstart",
            Valid.Replace("\"readme_quickstart\": \"dotnet run\"", string.Empty),
            "readme_quickstart");
    }

    private static void ModuleErrors()
    {
        ExpectField(
            "modules 非数组",
            Insert("\"modules\": {}"),
            "modules");

        ExpectField(
            "模块缺 key",
            Insert("\"modules\": [{ \"label\": \"应用\", \"paths\": [\"src\"] }]"),
            "modules");

        ExpectField(
            "模块缺 paths",
            Insert("\"modules\": [{ \"key\": \"app\", \"label\": \"应用\" }]"),
            "modules.app");

        ExpectField(
            "模块 key 重复",
            Insert("\"modules\": [{ \"key\": \"app\", \"label\": \"A\", \"paths\": [\"src\"] }, { \"key\": \"app\", \"label\": \"B\", \"paths\": [\"tests\"] }]"),
            "modules.app");
    }

    private static void OtherFieldErrors()
    {
        ExpectField("gitignore 非数组", Insert("\"gitignore\": \"bin\""), "gitignore");
        ExpectField("files 非对象", Insert("\"files\": []"), "files");
        ExpectField("naming 非对象", Insert("\"naming\": []"), "naming");
        ExpectField("naming 取值非法", Insert("\"naming\": { \"namespaceCase\": \"camel\" }"), "naming.namespaceCase");
        ExpectField("components 非数组", Insert("\"components\": {}"), "components");
    }

    private static void ConfigLevelErrors()
    {
        // 配置级错误没有可归属的模板：模板名必须为空，否则 UI 会提示用户去改一个不存在的模板
        ExpectField(
            "schemaVersion 过高",
            Valid.Replace("{\n  \"T\"", "{\n  \"$schemaVersion\": 2,\n  \"T\""),
            "$schemaVersion",
            expectedTemplate: null);

        ExpectField("顶层为空对象", "{}", null, expectedTemplate: null);
        ExpectField("JSON 语法错", "{ \"T\": ", null, expectedTemplate: null);

        var syntax = LoadError("{ \"T\": {");
        Check.That(
            syntax is not null && syntax.Details.Contains('行', StringComparison.Ordinal),
            "语法错应给出行列位置，用户才知道从哪开始看");

        // 模板体不是对象：能定位到模板名，但定位不到字段
        ExpectField("模板体不是对象", "{ \"T\": [] }", null);
    }

    private static void WarningsAreNotErrors()
    {
        var loader = new TemplateLoader("unused-user.json", "unused-install.json");
        var result = loader.LoadFromBytes(
            Encoding.UTF8.GetBytes(Valid.Replace(
                "\"structure\": { \"src\": [\"App\"] }",
                "\"structure\": { \"src\": [\"App\"] }, \"strcuture\": {}")),
            TemplateSource.User,
            "templates.json");

        Check.Equal(1, result.Templates.Count, "拼错的字段不应让整份配置失效");
        Check.That(
            result.Warnings.Any(line => line.Contains("strcuture", StringComparison.Ordinal)),
            "未知字段必须逐字回显，否则用户不知道是哪个键拼错了");
        Check.That(
            result.Warnings.Any(line => line.Contains("已忽略", StringComparison.Ordinal)),
            "警告要说清处理方式：忽略了还是照用了");
    }

    // ── 工具 ────────────────────────────────────────────────────────────

    private static string Insert(string members) => Valid.Replace(
        "\"structure\": {",
        members + ", \"structure\": {");

    private static void ExpectField(string label, string json, string? expectedField, string? expectedTemplate = "T")
    {
        var error = LoadError(json);
        if (error is null)
        {
            Check.That(false, $"{label}：应报错，但配置被当成合法接受了");
            return;
        }

        Check.Equal("PM-SCF-010", error.ErrorCode, $"{label}：错误码");
        Check.Equal(expectedTemplate, error.TemplateName, $"{label}：模板名");
        Check.Equal(expectedField, error.Field, $"{label}：字段");

        if (expectedField is not null)
        {
            Check.That(
                error.Message.Contains(expectedField, StringComparison.Ordinal),
                $"{label}：人读文案里也应出现字段名（实际：{error.Message}）");
        }

        // 配置级错误没有模板可归属，此时要求 Details 带模板名反而是错的
        if (expectedTemplate is not null)
        {
            Check.That(
                error.Details.Contains(expectedTemplate, StringComparison.Ordinal),
                $"{label}：Details 应带模板名，便于日志聚合（实际：{error.Details}）");
        }
    }

    private static void ExpectTooLarge(string label)
    {
        var builder = new StringBuilder();
        builder.Append("{ \"T\": { \"structure\": {");
        for (var index = 0; index < TemplateLoader.MaxDirectoriesPerTemplate + 1; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append($"\"d{index}\": []");
        }

        builder.Append("}, \"descriptions\": { \"x\": \"y\" }, \"readme_quickstart\": \"z\" } }");

        var error = LoadError(builder.ToString());
        if (error is null)
        {
            Check.That(false, $"{label}：应报超限");
            return;
        }

        Check.Equal("PM-SCF-011", error.ErrorCode, $"{label}：错误码");
        Check.Equal("T", error.TemplateName, $"{label}：模板名");
        Check.Equal("structure", error.Field, $"{label}：字段");
    }

    private static ScaffoldException? LoadError(string json)
    {
        var loader = new TemplateLoader("unused-user.json", "unused-install.json");
        try
        {
            loader.LoadFromBytes(Encoding.UTF8.GetBytes(json), TemplateSource.User, "templates.json");
            return null;
        }
        catch (ScaffoldException ex)
        {
            return ex;
        }
    }
}
