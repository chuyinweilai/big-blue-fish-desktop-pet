using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#if !COMPANION_SAFE
using BigBlueFish.Control;
using BigBlueFish.Agent;
using BigBlueFish.Deployment;
#endif

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private bool externalBubble { get; set; }
#if COMPANION_SAFE
    private bool AgentDailyActive => false;
    private void TickAgent(DateTime now) { }
    private bool DispatchAgentDaily() => false;
    private bool McpInputAvailable => false;
    private bool PuppetActive => false;
    private bool HasScheduledMcpSpeech => false;
    private void InitializeMcp() { preferences.McpEnabled = false; preferences.PuppetMode = false; preferences.McpWakeEnabled = false; }
    private void StopMcp() { }
    private bool PlayQueuedMcpAction() => false;
    private void PublishMcpInteraction(string kind, object detail) { }
    private bool ConsumeMcpSpeech() => false;
    private Action? AddMcpSettings(StackPanel panel, Window owner) => null;
#else
    private ControlServer? mcpServer;
    private AgentCoordinator? agent;
    private readonly string agentPipe = ControlTools.PipeName + "." + Guid.NewGuid().ToString("N");
    private readonly Queue<string> mcpActions = new();
    private string? mcpNextSpeech;
    private bool mcpConsentOpen;
    private Window? mcpConsentWindow;
    private bool McpInputAvailable => preferences.AgentEnabled;
    private bool PuppetActive => preferences.AgentEnabled && preferences.PuppetMode;
    private bool HasScheduledMcpSpeech => mcpNextSpeech != null;
    private bool AgentDailyActive => preferences.AgentEnabled && preferences.AgentDailySpeech;
    private void ConfigureAgent()
    {
        agent?.Configure(preferences.AgentHarnessId, new(preferences.AgentEnabled, preferences.PuppetMode, preferences.AgentDailySpeech));
        if (!AgentDailyActive) nextWarmup = DateTime.MinValue;
    }
    private bool agentLoading;
    private void TickAgent(DateTime now)
    {
        agent?.Tick(new DateTimeOffset(now), !hiddenByUser && !fullscreenHidden && !dragging && !exitAnimation && !companionClosed);
        bool loading = agent?.AwaitingSpeech == true && !hiddenByUser && !fullscreenHidden;
        if (loading != agentLoading || loading && loadingIndicator.Visibility != Visibility.Visible)
        {
            agentLoading = loading;
            if (loading) BeginModelSpeech(); else if (!speechRequestPending && !weatherRequestPending) EndModelSpeech();
        }
    }
    private bool DispatchAgentDaily()
    {
        if (!AgentDailyActive) return false;
        nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        agent?.DailyCare(); return true;
    }
    private void InitializeMcp()
    {
        if (!preferences.AgentEnabled) return;
        mcpServer ??= new ControlServer(ExecuteMcp, agentPipe);
        agent ??= new AgentCoordinator(Path.Combine(Environment.CurrentDirectory, "agent"),
            Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Mcp.exe"), agentPipe, RequestAgentPermission,
            App.Args.Contains("--test-mode") ? Path.Combine(Environment.CurrentDirectory, "agent-test-workspace") : null);
        if (!HarnessCatalog.All.Any(x => x.Id == preferences.AgentHarnessId)) preferences.AgentHarnessId = "codex";
        agent.LocalSpeechRequested = token => Dispatcher.InvokeAsync(() => RequestAgentLocalSpeech(token)).Task.Unwrap();
        ConfigureAgent();
        if (AgentDailyActive) brain.Stop();
    }
    private void StopMcp()
    {
        mcpConsentWindow?.Close(); mcpInput?.Close();
        agent?.Dispose(); agent = null;
        mcpServer?.Dispose(); mcpServer = null;
        mcpActions.Clear(); mcpNextSpeech = null;
    }
    private void SetPuppetMode(bool enabled)
    {
        if (preferences.PuppetMode == enabled) return;
        preferences.PuppetMode = enabled;
        InvalidateSpeechRequests(AgentDailyActive); EndGreetingNotice();
        if (!externalBubble) { speech.Cancel(); bubble.Visibility = Visibility.Collapsed; }
        CancelMovement(); ConfigureAgent(); RefreshMcpMenuAvailability();
        if (enabled && companionReady) PlayAnimation(animationPolicy.Find("待机呼吸休闲"), "PuppetIdle");
        if (!enabled)
        {
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
            nextWarmup = DateTime.MinValue; PlayIdle();
        }
    }
    private async Task RequestAgentLocalSpeech(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!preferences.AgentEnabled || AgentDailyActive) throw new InvalidOperationException("当前日常吐字由 Agent 编写；本地吐字需要关闭“把日常关心的吐字交给 Agent”。");
        if (!preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower) throw new InvalidOperationException("本地模型未启用、尚未安装或已按供电设置暂停。");
        if (speechRequestPending || companionClosed || exitAnimation || modelInstaller != null || hiddenByUser || fullscreenHidden) throw new InvalidOperationException("鱼鱼当前忙碌或隐藏，请稍后再触发本地吐字。");
        using var registration = token.Register(() => Dispatcher.BeginInvoke(() => InvalidateSpeechRequests()));
        await GenerateLocalSpeechAsync(true);
        token.ThrowIfCancellationRequested();
    }
    private void PublishMcpInteraction(string kind, object detail)
    {
        mcpServer?.Publish(kind, detail);
        if (kind == "user_message") return; // User submission uses the bounded ACP queue and only clears accepted text.
        if (kind == "click" && preferences.AgentRespondToClick || kind == "drag_end" && preferences.AgentRespondToDrag)
            agent?.Interaction(kind, detail);
    }
    private bool PlayQueuedMcpAction()
    {
        if (mcpActions.Count == 0) return false;
        CancelMovement();
        PlayAnimation(animationPolicy.Find(mcpActions.Dequeue()), "External");
        return true;
    }
    private void RefreshMcpMenuAvailability()
    {
        if (companionContextMenu != null) foreach (int index in new[] { 1, 2 }) if (companionContextMenu.Items[index] is MenuItem item) item.IsEnabled = true;
        if (notifyIcon?.ContextMenuStrip != null) foreach (int index in new[] { 2, 3 }) notifyIcon.ContextMenuStrip.Items[index].Enabled = true;
    }
    private void ShowMcpSpeech(string text)
    {
        agent?.MarkSpoken();
        InvalidateSpeechRequests(); EndGreetingNotice();
        externalBubble = true;
        bubbleIsModelReply = false;
        bubbleUntil = DateTime.MaxValue;
        speech.Show(text, true);
    }
    private bool ConsumeMcpSpeech()
    {
        if (mcpNextSpeech == null) return false;
        var text = mcpNextSpeech; mcpNextSpeech = null;
        nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        ShowMcpSpeech(text);
        return true;
    }
    private async Task<object> ExecuteMcp(string name, JsonElement args, CancellationToken token)
    {
        // All mutations, scheduling and target selection run on the ordinary WPF dispatcher.
        return await Dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (!preferences.McpEnabled || companionClosed || exitAnimation) throw new InvalidOperationException("MCP 接口已关闭或桌宠正在退出。");
            switch (name)
            {
                case "get_settings":
                    return ControlTools.Text(new { settings = CurrentMcpSettings(), state = new {
                        visible = IsVisible, hiddenByUser, fullscreenHidden, action = currentAction, animationPhase,
                        queuedActions = mcpActions.ToArray(), queuedSpeech = mcpNextSpeech, connections = mcpServer?.Connections ?? 0, inputOpen = mcpInput?.IsVisible == true, inputAvailable = McpInputAvailable,
                        agentConnected = agent?.Connected == true, agentStatus = agent?.Status, agentSession = agent?.SessionId, nextWake = agent?.NextWake,
                        modelAvailable = brain.ModelAvailable, modelBusy = brain.Busy, modelStatus = brain.Status } });
                case "update_settings":
                    await UpdateMcpSettings(args, token); return ControlTools.Text(new { settings = CurrentMcpSettings() });
                case "request_local_speech":
                    await RequestAgentLocalSpeech(token); return ControlTools.Text(new { generated = bubbleIsModelReply && bubble.Visibility == Visibility.Visible });
                case "list_actions":
                    return ControlTools.Text(animationPolicy.Rules.Select(r => new { action = r.Id, pool = r.Pool, weight = r.Weight, duration_seconds = r.Pool == "Greeting" ? 10 / 1.5 : 10 }));
                case "capture_foreground": case "capture_screen":
                    var handle = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
                    var identity = name == "capture_foreground" ? CompanionNative.ForegroundIdentity(handle) : "全部显示器组成的完整桌面";
                    var limit = ControlTools.ImageLimit(args, name == "capture_foreground" ? 2560 : 4096);
                    var image = await Task.Run(() => {
                        token.ThrowIfCancellationRequested();
                        return name == "capture_foreground" ? CompanionScreenCapture.Capture(handle, limit, true) : CompanionScreenCapture.CaptureScreen(limit);
                    }, token);
                    token.ThrowIfCancellationRequested();
                    if (!preferences.McpEnabled) throw new OperationCanceledException();
                    if (image == null) throw new IOException("未能截取屏幕。");
                    return ControlTools.Image(image.DataUrl, new { target = name, identity, image.Width, image.Height, capturedAt = DateTimeOffset.UtcNow });
                case "play_action": case "interrupt_action":
                    var action = ControlTools.RequiredText(args, "action", 200);
                    if (!companionReady || !animationPolicy.Rules.Any(r => r.Id == action)) throw new ArgumentException("动作不存在或播放器尚未就绪。");
                    if (name == "interrupt_action")
                    {
                        mcpActions.Clear(); CancelMovement();
                        PlayAnimation(animationPolicy.Find(action), "External");
                    }
                    else
                    {
                        if (mcpActions.Count >= 100) throw new InvalidOperationException("待播放动作队列已满。");
                        mcpActions.Enqueue(action);

                    }
                    return ControlTools.Text(new { action, started = name == "interrupt_action" || animationPhase == "External" && currentAction == action, queued = mcpActions.Count });
                case "say": case "say_next":
                    var text = ControlTools.RequiredText(args, "text");
                    agent?.MarkSpoken();
                    if (name == "say") ShowMcpSpeech(text); else mcpNextSpeech = text;
                    return ControlTools.Text(new { displayed = name == "say", pending = name == "say_next", text });
                case "get_operation_guide": case "get_wakeups": case "schedule_wakeup": case "cancel_wakeup":
                    return ControlTools.Text(agent?.ExecuteTool(name, args) ?? throw new InvalidOperationException("Agent 尚未连接。"));
                default: throw new ArgumentException("未知 MCP 工具。");
            }
        }).Task.Unwrap();
    }
    private Dictionary<string, object?> CurrentMcpSettings()
    {
        var result = typeof(CompanionPreferences).GetProperties().Where(p => !Attribute.IsDefined(p, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))).ToDictionary(p => p.Name, p => p.GetValue(preferences));
        result["StartupEnabled"] = StartupEnabled();
        return result;
    }
    private async Task UpdateMcpSettings(JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("settings", out var patch) || patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("settings 必须为对象。");
        var values = new Dictionary<string, object?>();
        foreach (var property in patch.EnumerateObject())
        {
            if (!ControlTools.SettingNames.Contains(property.Name)) throw new ArgumentException(property.Name == "McpEnabled" ? "MCP 接口只能由用户在设置中开启或关闭。" : "未知设置：" + property.Name);
            if (property.Name == "StartupEnabled") { values[property.Name] = property.Value.GetBoolean(); continue; }
            var field = typeof(CompanionPreferences).GetProperty(property.Name)!;
            values[property.Name] = JsonSerializer.Deserialize(property.Value.GetRawText(), field.PropertyType);
        }
        // Validate the entire patch before applying any part of it.
        foreach (var item in values)
        {
            bool Range(double low, double high) => item.Value is IConvertible c && double.IsFinite(c.ToDouble(null)) && c.ToDouble(null) >= low && c.ToDouble(null) <= high;
            bool valid = item.Key switch {
                "Scale" or "BubbleScale" => Range(.5, 2),
                "AnimationIntervalSeconds" => Range(30, 300),
                "BreakfastHour" or "LunchHour" or "DinnerHour" => Range(0, 23),
                "BubbleDurationIndex" => Range(0, 7),
                "SpeechMinMinutes" => item.Value is int minutes && CompanionPreferences.SpeechRanges.Any(r => r.Min == minutes),
                "WeatherRegionCode" => item.Value is string region && CompanionRegions.District(region).Code == region,
                "LastBrushDate" => item.Value == null || item.Value is string date && DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
                "Left" or "Top" => item.Value == null || Range(-1000000, 1000000),
                _ => item.Value is bool };
            if (!valid) throw new ArgumentException("设置值无效：" + item.Key);
        }
        if (values.TryGetValue("PuppetMode", out var puppet) && puppet is true && !preferences.PuppetMode
            && !await ConfirmMcp(companionSettings, ControlTools.BehaviorControlTitle, ControlTools.PuppetWarning, token)) throw new OperationCanceledException("用户取消让 Agent 安排日常关心与动作。");
        token.ThrowIfCancellationRequested();
        if (!preferences.McpEnabled) throw new OperationCanceledException();
        bool oldGpu = preferences.GpuEnabled, oldModel = preferences.ModelEnabled;
        string oldRegion = preferences.WeatherRegionCode;
        foreach (var item in values)
        {
            if (item.Key == "StartupEnabled") SetCompanionStartup((bool)item.Value!);
            else if (item.Key != "PuppetMode") typeof(CompanionPreferences).GetProperty(item.Key)!.SetValue(preferences, item.Value);
        }
        if (values.TryGetValue("PuppetMode", out puppet)) SetPuppetMode((bool)puppet!);
        if (oldGpu != preferences.GpuEnabled || oldModel && !preferences.ModelEnabled) InvalidateSpeechRequests(true);
        brain.UseGpu = preferences.GpuEnabled; brain.WeatherRegionCode = preferences.WeatherRegionCode;
        if (oldRegion != preferences.WeatherRegionCode) InvalidateSpeechRequests();
        ApplyCompanionPowerPolicy(); ApplyCompanionScale(false); ApplyCompanionBubbleScale(false);
        if (values.TryGetValue("Left", out var left) && left is double x) Left = x;
        if (values.TryGetValue("Top", out var top) && top is double y) Top = y;
        ClampCompanion(); anchor = new(Left, Top);
        if (!preferences.HideOnFullscreen && fullscreenHidden) { fullscreenHidden = false; if (!hiddenByUser) Show(); }
        if (values.ContainsKey("SpeechMinMinutes")) nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        if (values.ContainsKey("AnimationIntervalSeconds")) nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
        if (values.ContainsKey("BubbleDurationIndex") && bubble.Visibility == Visibility.Visible) ResetBubbleDeadline();
        ConfigureAgent();
        if (!ChatInputAvailable) mcpInput?.Close();
        UpdateCompanionVisibility();
        SaveCompanion();
        refreshCompanionSettings?.Invoke();
    }
    private async Task<bool> ConfirmMcp(Window? owner, string title, string message, CancellationToken token = default, string accept = "了解并开启")
    {
        if (mcpConsentOpen) throw new InvalidOperationException("请先处理已打开的 MCP 提示窗口。");
        mcpConsentOpen = true;
        try
        {
            var dialog = new ModernWindow(title, title, message, 640, 460);
            if (owner?.IsVisible == true) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            bool accepted = false;
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => finished.TrySetResult(accepted && !token.IsCancellationRequested);
            mcpConsentWindow = dialog;
            dialog.Actions.Children.Add(ModernUI.Button(accept, () => { accepted = true; dialog.Close(); }, true));
            dialog.Actions.Children.Add(ModernUI.Button("取消", dialog.Close));
            using var registration = token.Register(() => Dispatcher.BeginInvoke(() => { accepted = false; dialog.Close(); }));
            dialog.Show();
            return await finished.Task;
        }
        finally { mcpConsentWindow = null; mcpConsentOpen = false; }
    }
#endif
}
