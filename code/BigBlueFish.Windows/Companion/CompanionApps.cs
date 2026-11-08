using System;
using System.Diagnostics;
using System.Linq;

namespace BigBlueFish.Windows;

// Lets the owner open common local apps from chat. Only a fixed whitelist can be
// launched, and the message must read like a request, so a stray word never runs
// anything by accident.
internal static class CompanionApps
{
    private sealed record Entry(string Name, string Target, string[] Aliases);

    private static readonly Entry[] Known =
    [
        new("微信", "weixin://", ["微信", "wechat"]),
        new("QQ", "tencent://", ["qq"]),
        new("浏览器", "https://cn.bing.com", ["浏览器", "网页", "edge"]),
        new("记事本", "notepad.exe", ["记事本", "notepad"]),
        new("计算器", "calc.exe", ["计算器", "calc"]),
        new("文件资源管理器", "explorer.exe", ["资源管理器", "文件管理", "我的电脑", "此电脑"]),
        new("任务管理器", "taskmgr.exe", ["任务管理器"]),
        new("系统设置", "ms-settings:", ["系统设置", "设置页", "windows设置"]),
        new("终端", "wt.exe", ["终端", "命令行", "powershell", "cmd"]),
        new("画图", "mspaint.exe", ["画图", "paint"]),
        new("截图工具", "snippingtool.exe", ["截图"]),
        new("回收站", "shell:RecycleBinFolder", ["回收站"])
    ];

    private static readonly string[] Verbs = ["帮我打开", "给我打开", "打开一下", "启动", "运行", "打开", "开一下"];

    internal static bool TryLaunch(string prompt, out string message)
    {
        message = "";
        var text = prompt.Trim();
        // Long sentences are chat, not commands; keep the trigger narrow.
        if (text.Length == 0 || text.Length > 30) return false;
        if (!Verbs.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase))) return false;
        var entry = Known.FirstOrDefault(app => app.Aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase)));
        if (entry == null) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
            message = "好嘞，鱼鱼把" + entry.Name + "打开啦～";
            return true;
        }
        catch (Exception)
        {
            message = "唔……鱼鱼没能打开" + entry.Name + "，这台电脑上好像没有诶。";
            return true;
        }
    }
}
