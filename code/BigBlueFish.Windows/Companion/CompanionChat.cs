using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BigBlueFish.Deployment;

namespace BigBlueFish.Windows;

// The chat box works with either brain: an ACP agent when one is connected,
// otherwise the local model. Both paths share the same bubble output.
public partial class MainWindow
{
    private const int ChatHistoryEntries = 6;
    private const int ChatHistoryChars = 800;
    private readonly List<(bool FromUser, string Text)> chatHistory = new();
    private bool localChatPending;
    private Window? mcpInput;

    private bool ChatInputAvailable => McpInputAvailable || LocalChatAvailable;
    private bool LocalChatAvailable => !companionClosed && !exitAnimation && preferences.ModelEnabled
        && brain.ModelAvailable && brain.CanRunOnCurrentPower;
    private bool LocalChatReady => LocalChatAvailable && !localChatPending;

#if COMPANION_SAFE
    private bool AgentChatReady => false;
    private bool AgentChatBusy => false;
    private string? AgentChatDetail => null;
    private bool AgentChatSend(string text) => false;
#else
    private bool AgentChatReady => agent is { Connected: true };
    private bool AgentChatBusy => agent is { Busy: true };
    private string? AgentChatDetail => agent?.Status;
    private bool AgentChatSend(string text) => agent is { Connected: true } ready && ready.SendUser(text);
#endif

    private bool TryOpenMcpInput()
    {
        if (!ChatInputAvailable || exitAnimation || companionClosed) return false;
        if (mcpInput != null) { mcpInput.Show(); mcpInput.Activate(); return true; }
        const string agentHint = "通过鱼鱼和已连接的Agent说话";
        const string localHint = "和鱼鱼说点什么……";
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000,
            Height = 56, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14, Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 22, 0) };
        var placeholder = new TextBlock { Text = AgentChatReady ? agentHint : localHint, Foreground = Brushes.Gray, FontSize = 14,
            Margin = new Thickness(2, 4, 22, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetName(text, localHint);
        var editor = new Grid(); editor.Children.Add(text); editor.Children.Add(placeholder);
        var status = new TextBlock { Text = "Ctrl+Enter 发送", Foreground = Brushes.SlateGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        var content = new StackPanel(); content.Children.Add(editor);
        var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 251, 244)), BorderBrush = blue,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = content };
        var window = new Window { Title = "蓝色大肥鱼 · 输入", Width = 354, Height = 120, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Owner = this, Content = frame, FontFamily = new FontFamily("Microsoft YaHei UI") };
        using (var theme = typeof(ModernWindow).Assembly.GetManifestResourceStream("BigBlueFish.ModernTheme.xaml")!)
        {
            var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(theme);
            window.Resources[typeof(Button)] = resources[typeof(Button)];
        }
        var close = new Button { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 16,
            MinHeight = 0, Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "收起输入框" };
        close.Click += (_, _) => window.Close(); editor.Children.Add(close);
        void Send()
        {
            if (string.IsNullOrWhiteSpace(text.Text)) return;
            var message = text.Text.Trim();
            if (AgentChatReady)
            {
                if (!AgentChatSend(message)) { status.Text = AgentChatDetail ?? "Agent 尚未连接"; return; }
                PublishMcpInteraction("user_message", new { text = message });
                text.Clear(); status.Text = "已发送给 Agent"; text.Focus();
                return;
            }
            if (!LocalChatReady) { status.Text = LocalChatBlocker(); return; }
            text.Clear(); text.Focus();
            _ = SendLocalChatAsync(message);
        }
        var send = ModernUI.Button("发送", Send, true); send.MinWidth = 58; send.MinHeight = 0; send.Margin = new Thickness(0); send.Padding = new Thickness(12, 5, 12, 5); send.FontSize = 12;
        var actions = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(status); actions.Children.Add(send); Grid.SetColumn(send, 1); content.Children.Add(actions);
        void Refresh()
        {
            bool agent = AgentChatReady;
            placeholder.Text = agent ? agentHint : localHint;
            placeholder.Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            send.IsEnabled = text.Text.Length > 0 && (agent || LocalChatReady);
        }
        text.TextChanged += (_, _) => Refresh();
        window.PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; window.Close(); }
            else if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; Send(); }
        };
        bool positioning = false;
        void Follow()
        {
            if (positioning || mcpInput != window) return;
            positioning = true;
            try {
                if (!IsVisible) { window.Hide(); return; }
                var area = CompanionEdgeLayout.WorkArea(this); var body = CompanionEdgeLayout.PetBounds(this);
                window.Width = Math.Min(354, Math.Max(160, area.Width - 16));
                double x = Left + body.Left - window.Width - 8;
                if (x < area.Left + 8) x = Left + body.Right + 8;
                double y = Top + body.Bottom - window.Height;
                window.Left = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.Width - 8));
                window.Top = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.Height - 8));
                if (!window.IsVisible) window.Show();
            } finally { positioning = false; }
        }
        EventHandler moved = (_, _) => Follow();
        SizeChangedEventHandler resized = (_, _) => Follow();
        DependencyPropertyChangedEventHandler visibility = (_, _) => Follow();
        DpiChangedEventHandler dpiChanged = (_, _) => Follow();
        LocationChanged += moved; SizeChanged += resized; IsVisibleChanged += visibility; DpiChanged += dpiChanged;
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        clock.Tick += (_, _) => {
            if (!ChatInputAvailable) { window.Close(); return; }
            Follow();
            Refresh();
            if (AgentChatReady) status.Text = status.Text == "已发送给 Agent" ? status.Text : AgentChatBusy ? "Agent 正在处理…" : "Ctrl+Enter 发送";
            else if (localChatPending) status.Text = "鱼鱼正在想怎么回答…";
            else status.Text = LocalChatReady ? "Ctrl+Enter 发送" : LocalChatBlocker();
        };
        clock.Start(); mcpInput = window;
        window.Closed += (_, _) => { clock.Stop(); LocationChanged -= moved; SizeChanged -= resized; IsVisibleChanged -= visibility; DpiChanged -= dpiChanged; mcpInput = null; };
        window.Loaded += (_, _) => { text.Focus(); Refresh(); };
        Follow(); window.Activate(); return true;
    }

    private string LocalChatBlocker()
    {
        if (!preferences.ModelEnabled) return "请先在设置里启用本地大模型";
        if (!brain.ModelAvailable) return "还没有安装本地模型，可以在设置里安装";
        if (!brain.CanRunOnCurrentPower) return "当前按供电设置暂停了本地模型";
        return "鱼鱼正在忙着说别的话，稍等一下";
    }

    private CompanionBrain.ChatMessage[] ChatMessages(string prompt)
    {
        var selected = new List<(bool FromUser, string Text)>();
        int budget = ChatHistoryChars;
        for (int i = chatHistory.Count - 1; i >= 0; i--)
        {
            var entry = chatHistory[i];
            if (entry.Text.Length > budget && selected.Count > 0) break;
            budget -= entry.Text.Length;
            selected.Insert(0, entry);
            if (selected.Count >= ChatHistoryEntries) break;
        }
        var messages = new List<CompanionBrain.ChatMessage> { new("system", CompanionDialogue.ChatPersona) };
        foreach (var entry in selected) messages.Add(new(entry.FromUser ? "user" : "assistant", entry.Text));
        messages.Add(new("user", prompt));
        return messages.ToArray();
    }

    private async Task SendLocalChatAsync(string prompt)
    {
        if (localChatPending || companionClosed || exitAnimation || !LocalChatAvailable) return;
        localChatPending = true;
        var messages = ChatMessages(prompt);
        chatHistory.Add((true, prompt));
        while (chatHistory.Count > ChatHistoryEntries) chatHistory.RemoveAt(0);
        try
        {
            // Give an in-flight warm-up or utterance the same window manual
            // requests use, then take the model over for this turn.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (companionClosed || exitAnimation) return;
            if (!LocalChatAvailable || brain.Busy)
            {
                ShowCompanionNotice("主人，鱼鱼还在忙着别的事，等一下再跟我说吧～", true, true);
                return;
            }
            InvalidateSpeechRequests();
            var version = speechRequestVersion;
            bool streamed = false;
            BeginModelSpeech();
            string reply;
            try
            {
                reply = await brain.Chat(messages, partial =>
                {
                    streamed = true;
                    ShowCompanionPartial(partial, version, true);
                });
            }
            finally
            {
                if (version == speechRequestVersion) EndModelSpeech();
            }
            if (version != speechRequestVersion || companionClosed || exitAnimation) return;
            if (reply.Length == 0)
            {
                if (streamed) ClearCompanionModelReply();
                ShowCompanionNotice("主人，鱼鱼刚刚没想好怎么回答，再说一次好不好～", true, true);
                return;
            }
            chatHistory.Add((false, reply));
            while (chatHistory.Count > ChatHistoryEntries) chatHistory.RemoveAt(0);
            if (streamed) FinishCompanionPartial(reply, true);
            else ShowCompanionBubble(reply, true);
        }
        finally
        {
            localChatPending = false;
        }
    }
}
