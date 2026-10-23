using ProjectManager.Application.Tests;

// M2 测试入口：退出码 0 表示全部通过。
try
{
    StorageTests.Run();
    WizardTests.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"\n未捕获异常：{ex}");
    return 2;
}

return Check.Report();
