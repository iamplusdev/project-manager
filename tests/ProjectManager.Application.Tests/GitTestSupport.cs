using System.Diagnostics;
using System.Text;
using ProjectManager.CliInterop;

namespace ProjectManager.Application.Tests;

/// <summary>
/// 真 git 测试的共用辅助。抽出来是因为历史与工作台两组用例都要建仓库、配身份、跑 git，
/// 各留一份的话「怎么配置才不依赖开发机全局设置」这个知识就有了两个可能走样的副本。
/// </summary>
internal static class GitTestSupport
{
    public static bool GitAvailable() => new GitRunner().IsAvailableAsync().GetAwaiter().GetResult();

    /// <summary>提交需要身份；测试仓库显式配置，避免依赖开发机的全局 git 配置。</summary>
    public static void ConfigureIdentity(string root)
    {
        RunGit(root, "config", "user.name", "PM Test");
        RunGit(root, "config", "user.email", "pm-test@example.invalid");

        // 关掉提交签名：有没有 gpg 不该影响测试结论
        RunGit(root, "config", "commit.gpgsign", "false");
    }

    /// <summary>建一个已 init 且配好身份的临时仓库。</summary>
    public static GitRunner CreateRepository(string root)
    {
        var git = new GitRunner();
        git.InitAsync(root).GetAwaiter().GetResult();
        ConfigureIdentity(root);
        return git;
    }

    /// <summary>在指定目录里写文件并提交。<paramref name="paths"/> 为空时提交全部改动。</summary>
    public static void CommitAll(string root, string message, string? body = null)
    {
        RunGit(root, "add", "-A");

        if (body is null)
        {
            RunGit(root, "commit", "-q", "-m", message);
        }
        else
        {
            RunGit(root, "commit", "-q", "-m", message, "-m", body);
        }
    }

    public static (int ExitCode, string Output) RunGit(string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return (process.ExitCode, output);
    }
}
