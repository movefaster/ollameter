using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Ollameter.Core;

namespace Ollameter;

/// <summary>Names the computer a section belongs to. One computer on its own needs no name unless it's another one.</summary>
internal sealed record SectionHeading(string Host, bool IsLocal);

/// <summary>The window's controls live in one section only: the top one when stacked, the right one in columns.</summary>
internal sealed record SectionControls(bool ShowsCompactToggle);

internal static class MeterLayout
{
    public const double Width = 400;
    public const double Gutter = 16;
}

/// <summary>
/// Every watched computer: stacked sections when compact, side-by-side columns when expanded. Sections are built
/// once and updated in place, so a button stays under the pointer while the numbers change.
/// </summary>
internal sealed class MeterBoard : Border
{
    private readonly string localHost;
    private readonly List<SourceSection> sections = [];
    private (int Count, bool Compact, bool Dark, string Identity)? structure;

    public Action ToggleCompact { get; init; } = () => { };
    public Action Close { get; init; } = () => { };
    public Action<string> Unload { get; init; } = _ => { };

    public MeterBoard(string localHost)
    {
        this.localHost = localHost;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    public void Update(IReadOnlyList<MeterViewModel> models, bool compact, Theme theme)
    {
        // Rebuilt only when sections come or go, or their arrangement changes.
        var identity = string.Join("|", models.Select(model => model.Remote is null ? "local" : "remote"));
        var next = (models.Count, compact, theme.IsDark, identity);
        if (structure != next) Rebuild(models.Count, compact, theme);
        structure = next;
        for (var index = 0; index < models.Count; index++)
            sections[index].Update(models[index], Heading(models, index), Controls(models, index, compact));
    }

    private SectionHeading? Heading(IReadOnlyList<MeterViewModel> models, int index)
    {
        var model = models[index];
        if (model.Remote is { } remote) return new SectionHeading(remote.Host, false);
        return models.Count > 1 ? new SectionHeading(localHost, true) : null;
    }

    private static SectionControls? Controls(IReadOnlyList<MeterViewModel> models, int index, bool compact)
    {
        if (index != (compact ? 0 : models.Count - 1)) return null;
        // An offline computer looks the same either way, so the toggle only shows when it would change something.
        return new SectionControls(models.Any(model => model.Display.DisplayPhase != Phase.Offline));
    }

    private void Rebuild(int count, bool compact, Theme theme)
    {
        sections.Clear();
        Background = theme.Surface;
        for (var index = 0; index < count; index++)
            sections.Add(new SourceSection(theme, compact, ToggleCompact, Close, Unload));
        if (compact)
        {
            var stack = new StackPanel();
            for (var index = 0; index < count; index++)
            {
                if (index > 0) stack.Children.Add(Ui.Hairline(theme));
                stack.Children.Add(sections[index]);
            }
            Child = stack;
        }
        else
        {
            // Columns share the tallest one's height, so the dividers run the full length.
            var grid = new Grid();
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                    var divider = new Rectangle { Fill = theme.Hairline, Width = 1 };
                    Grid.SetColumn(divider, grid.ColumnDefinitions.Count - 1);
                    grid.Children.Add(divider);
                }
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                sections[index].VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(sections[index], grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(sections[index]);
            }
            Child = grid;
        }
    }
}

/// <summary>One computer's section of the window, as a function of its view model.</summary>
internal sealed class SourceSection : Border
{
    private readonly Theme theme;
    private readonly bool compact;
    private readonly HeaderView header;
    private readonly HeroView hero;
    private readonly CompactRowsView? compactRows;
    private readonly UIElement[] expandedOnly = [];
    private readonly SpeedView? speed;
    private readonly EfficiencyView? efficiency;
    private readonly UsageBar? context;
    private readonly PromptCacheView? promptCache;
    private readonly HardwareView? hardware;
    private readonly Rectangle? modelsLine, noticesLine, requestsLine;
    private readonly ModelsView? models;
    private readonly NoticesView? notices;
    private readonly RequestsView? requests;
    private readonly TextBlock? footer;

    public SourceSection(Theme theme, bool compact, Action toggleCompact, Action close, Action<string> unload)
    {
        this.theme = theme;
        this.compact = compact;
        Width = MeterLayout.Width;
        Padding = new Thickness(MeterLayout.Gutter, 10, MeterLayout.Gutter, 14);
        header = new HeaderView(theme, compact, toggleCompact, close, unload);
        hero = new HeroView(theme);
        var stack = new Stack { Spacing = 12 }.With(header, hero);
        if (compact)
        {
            compactRows = new CompactRowsView(theme);
            stack.Children.Add(compactRows);
        }
        else
        {
            speed = new SpeedView(theme);
            efficiency = new EfficiencyView(theme);
            context = new UsageBar(theme, "Context used");
            promptCache = new PromptCacheView(theme);
            hardware = new HardwareView(theme);
            modelsLine = Ui.Hairline(theme);
            models = new ModelsView(theme);
            noticesLine = Ui.Hairline(theme);
            notices = new NoticesView(theme);
            requestsLine = Ui.Hairline(theme);
            requests = new RequestsView(theme);
            footer = Ui.Text(11, theme.Ink2);
            expandedOnly =
            [
                Ui.Hairline(theme), speed, Ui.Hairline(theme), efficiency, context, promptCache, Ui.Hairline(theme),
                hardware, modelsLine, models, noticesLine, notices, requestsLine, requests, footer,
            ];
            foreach (var element in expandedOnly) stack.Children.Add(element);
        }
        Child = stack;
    }

    public void Update(MeterViewModel model, SectionHeading? heading, SectionControls? controls)
    {
        header.Update(model, heading, controls);
        hero.Update(model);
        var offline = model.Display.DisplayPhase == Phase.Offline;
        if (compact)
        {
            compactRows!.Show(!offline);
            if (!offline) compactRows!.Update(model);
            return;
        }
        foreach (var element in expandedOnly) element.Show(!offline);
        if (offline) return;

        speed!.Update(model.Stats);
        efficiency!.Update(model.Stats);
        if (model.Snapshot.ContextUsed is { } used && model.Snapshot.ContextSize is { } size && size > 0)
        {
            var fraction = (double)used / size;
            var severity = fraction >= 0.95 ? theme.Critical : fraction >= 0.8 ? theme.Warning : null;
            context!.Update($"{Format.Integer(used)} of {Format.Integer(size)} · {Format.Percent(fraction)}", fraction, severity);
        }
        else
        {
            context!.Show(false);
        }
        if (model.PromptCache is { } cache && model.Models.Count > 0) promptCache!.Update(cache);
        else promptCache!.Show(false);
        hardware!.Update(model);
        var manyModels = model.Models.Count > 1;
        modelsLine!.Show(manyModels);
        models!.Show(manyModels);
        if (manyModels) models!.Update(model.Models, model.Now);
        var visibleNotices = model.VisibleNotices;
        noticesLine!.Show(visibleNotices.Count > 0);
        notices!.Show(visibleNotices.Count > 0);
        if (visibleNotices.Count > 0) notices!.Update(visibleNotices);
        requestsLine!.Show(model.Requests.Count > 0);
        requests!.Show(model.Requests.Count > 0);
        if (model.Requests.Count > 0) requests!.Update(model.Requests.Take(6).ToList(), model.Now);
        footer!.Show(model.Stats.Requests > 0);
        footer!.Set(
            $"Since this model loaded: {Format.Integer(model.Stats.Requests)} requests, {Format.Integer(model.Stats.GeneratedTokens)} tokens generated");
    }
}

internal sealed class HeaderView : Stack
{
    public const double ButtonSize = 24;

    private readonly Theme theme;
    private readonly bool compact;
    private readonly ContentControl leading = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly PhaseLabel phaseTop;
    private readonly PhaseLabel phaseBelow;
    private readonly HostLabel host;
    private readonly FlatButton compactToggle;
    private readonly FlatButton close;
    private readonly Grid modelRow;
    private readonly TextBlock modelName;
    private readonly UnloadButton unload;
    private readonly TextBlock details;

    public HeaderView(Theme theme, bool compact, Action toggleCompact, Action closeWindow, Action<string> onUnload)
        : base()
    {
        this.theme = theme;
        this.compact = compact;
        phaseTop = new PhaseLabel(theme);
        phaseBelow = new PhaseLabel(theme) { Margin = new Thickness(0, 2, 0, 0) };
        host = new HostLabel(theme);

        compactToggle = IconButton(compact ? Glyphs.ChevronDown(theme.Ink2) : Glyphs.ChevronUp(theme.Ink2), compact ? "Show all metrics" : "Show less");
        compactToggle.Click += toggleCompact;
        close = IconButton(Glyphs.Close(theme.Ink2), "Hide window");
        close.Click += closeWindow;
        var buttons = new Stack { Orientation = Orientation.Horizontal }.With(compactToggle, close);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var top = Ui.Columns((Ui.Auto, leading), (Ui.Star, new Border()), (Ui.Auto, buttons));
        // Sections without the window's buttons keep the same height as the one with them.
        top.MinHeight = ButtonSize;

        modelName = Ui.Text(13, theme.Ink, FontWeights.Medium);
        modelName.VerticalAlignment = VerticalAlignment.Center;
        unload = new UnloadButton(theme);
        unload.Pressed += name => onUnload(name);
        unload.Margin = new Thickness(8, 0, 0, 0);
        modelRow = Ui.Columns((Ui.Star, modelName), (Ui.Auto, unload));
        // Keeps the window from changing height when the button comes and goes.
        modelRow.MinHeight = UnloadButton.ButtonHeight;

        details = Ui.Text(11, theme.Ink2);
        Spacing = 2;
        Children.Add(top);
        Children.Add(phaseBelow);
        Children.Add(modelRow);
        Children.Add(details);
    }

    private FlatButton IconButton(UIElement glyph, string help)
    {
        var button = new FlatButton(glyph, Brushes.Transparent, theme.HoverFill, new CornerRadius(5))
        {
            Width = ButtonSize, Height = ButtonSize, ToolTip = help,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, help);
        return button;
    }

    public void Update(MeterViewModel model, SectionHeading? heading, SectionControls? controls)
    {
        // The computer's name goes first, so it's clear whose numbers these are.
        if (heading is not null)
        {
            host.Update(heading);
            if (leading.Content != host) leading.Content = host;
        }
        else if (leading.Content != phaseTop)
        {
            leading.Content = phaseTop;
        }
        phaseTop.Update(model);
        phaseBelow.Update(model);
        phaseBelow.Show(heading is not null);

        compactToggle.Show(controls?.ShowsCompactToggle == true);
        close.Show(controls is not null);

        modelRow.Show(model.Remote?.Problem is null);
        modelName.Set(model.Models.FirstOrDefault()?.Name ?? "No model loaded");
        // Sharing is read-only, so only this PC's models can be unloaded.
        var unloadable = model.Remote is null && model.Models.Count > 0;
        unload.Visibility = unloadable ? Visibility.Visible : Visibility.Collapsed;
        if (unloadable) unload.Update(model, model.Models[0].Name);

        var showDetails = !compact && model.Display.DisplayPhase != Phase.Offline;
        details.Show(showDetails);
        if (showDetails) details.Set(Details(model));
    }

    private static string Details(MeterViewModel model)
    {
        if (model.Models.FirstOrDefault() is not { } loaded) return model.Version is { } version ? $"Ollama {version}" : "Ollama";
        var parts = new List<string>();
        if (loaded.ParameterSize is { } parameters) parts.Add(parameters);
        if (loaded.Quantization is { } quantization) parts.Add(quantization);
        if (loaded.ContextLength is { } context) parts.Add($"{Format.CompactTokens(context)} ctx");
        if (loaded.ExpiresAt is { } expires && expires > model.Now)
            parts.Add($"unloads in {Format.Countdown((expires - model.Now).TotalSeconds)}");
        return string.Join(" · ", parts);
    }
}

internal sealed class PhaseLabel : Stack
{
    private readonly Theme theme;
    private readonly Ellipse dot;
    private readonly TextBlock name;

    public PhaseLabel(Theme theme)
    {
        this.theme = theme;
        Orientation = Orientation.Horizontal;
        Spacing = 7;
        dot = Ui.Dot(theme.Muted);
        name = Ui.Text(12, theme.Ink, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(dot);
        Children.Add(name);
    }

    public void Update(MeterViewModel model)
    {
        var (text, brush) = Describe(model);
        name.Set(text);
        if (dot.Fill != brush) dot.Fill = brush;
    }

    private (string, Brush) Describe(MeterViewModel model)
    {
        switch (model.Remote?.Status.Kind)
        {
            case RemoteStatusKind.Connecting: return ("Connecting", theme.Muted);
            case RemoteStatusKind.Unreachable: return ("Not connected", theme.Critical);
            case RemoteStatusKind.Incompatible: return ("Can't read this computer", theme.Warning);
        }
        return model.Display.DisplayPhase switch
        {
            Phase.Offline => ("Offline", theme.Critical),
            Phase.Idle => ("Idle", theme.Muted),
            Phase.Loading => ("Loading model", theme.Prompt),
            Phase.Prefill => ("Reading prompt", theme.Prompt),
            _ => ("Generating", theme.Generation),
        };
    }
}

internal sealed class HostLabel : Stack
{
    private readonly TextBlock name;
    private readonly TextBlock local;

    public HostLabel(Theme theme)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        var glyph = Glyphs.Computer(theme.Ink2);
        name = Ui.Text(13, theme.Ink, FontWeights.SemiBold);
        name.MaxWidth = 250;
        local = Ui.Text(11, theme.Ink2, text: "This PC");
        local.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(glyph);
        Children.Add(name);
        Children.Add(local);
    }

    public void Update(SectionHeading heading)
    {
        name.Set(heading.Host);
        local.Show(heading.IsLocal);
        ToolTip = heading.IsLocal
            ? "This PC's Ollama. Right-click the tray icon to choose which computers to show."
            : $"{heading.Host}'s Ollama. Right-click the tray icon to choose which computers to show.";
        System.Windows.Automation.AutomationProperties.SetName(this, heading.IsLocal ? $"{heading.Host}, this PC" : heading.Host);
    }
}

internal sealed class UnloadButton : Border
{
    public const double ButtonHeight = 20;

    private enum Look { Ready, Working, Confirming, Unloading, Failed }

    private readonly Theme theme;
    private readonly FlatButton button;
    private readonly Path eject;
    private readonly Path warning;
    private readonly TextBlock title;
    private string name = "";

    public event Action<string>? Pressed;

    public UnloadButton(Theme theme)
    {
        this.theme = theme;
        eject = Glyphs.Eject(theme.Ink2);
        warning = Glyphs.Warning(theme.Warning);
        title = Ui.Text(11, theme.Ink2, FontWeights.Medium);
        title.VerticalAlignment = VerticalAlignment.Center;
        var content = new Stack { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0, 6, 0) }
            .With(eject, warning, title);
        content.VerticalAlignment = VerticalAlignment.Center;
        button = new FlatButton(content, theme.Hairline, theme.HoverFill, new CornerRadius(5)) { Height = ButtonHeight };
        button.Click += () => Pressed?.Invoke(name);
        Child = button;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Update(MeterViewModel model, string modelName)
    {
        name = modelName;
        var look = model.Unload switch
        {
            { Kind: UnloadKind.Unloading } state when state.Model == modelName => Look.Unloading,
            { Kind: UnloadKind.Confirming } state when state.Model == modelName && !model.IsWorking => Look.Confirming,
            { Kind: UnloadKind.Failed } state when state.Model == modelName => Look.Failed,
            _ => model.IsWorking ? Look.Working : Look.Ready,
        };
        title.Set(look switch
        {
            Look.Confirming => "Click again to unload",
            Look.Unloading => "Unloading…",
            Look.Failed => "Couldn't unload",
            _ => "Unload",
        });
        var help = look switch
        {
            Look.Ready or Look.Confirming =>
                $"Unload {modelName} from memory now. Its saved prompts go too, so the next request reads its whole prompt again.",
            Look.Working => "Available when the model is idle.",
            Look.Unloading => "Waiting for Ollama to free the model's memory.",
            _ => "Ollama didn't unload the model. Click to try again.",
        };
        var isWarning = look is Look.Confirming or Look.Failed;
        eject.Show(!isWarning);
        warning.Show(isWarning);
        title.Foreground = isWarning ? theme.Ink : theme.Ink2;
        button.Enabled = look is not (Look.Working or Look.Unloading);
        Opacity = look == Look.Working ? 0.5 : 1;
        // A disabled element shows no tooltip, so it sits on the wrapper.
        ToolTip = help;
        ToolTipService.SetShowOnDisabled(this, true);
        System.Windows.Automation.AutomationProperties.SetName(button, title.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, help);
    }
}

internal sealed class HeroView : Stack
{
    private readonly Theme theme;
    private readonly TextBlock valueBlock;
    private readonly Run value;
    private readonly Run unit;
    private readonly Stack sparkColumn;
    private readonly Sparkline sparkline;
    private readonly TextBlock detail;
    private readonly MeterBar promptBar;

    public HeroView(Theme theme)
    {
        this.theme = theme;
        Spacing = 8;
        value = new Run { FontSize = 52, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont };
        unit = new Run { FontSize = 15, Foreground = theme.Ink2 };
        // The font's own line box leaves a large gap under 52-point digits, so the line is set to the digits' height.
        valueBlock = new TextBlock
        {
            FontFamily = Theme.Font, TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Bottom,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 56,
        };
        valueBlock.Inlines.Add(value);
        valueBlock.Inlines.Add(unit);
        sparkline = new Sparkline(theme.GenerationColor, theme.Hairline, MeterViewModel.HistoryLimit)
        {
            Width = 130, Height = 40, ToolTip = "Generation speed over the last 60 seconds",
        };
        var caption = Ui.Text(11, theme.Ink2, text: "t/s, last 60 s");
        caption.HorizontalAlignment = HorizontalAlignment.Right;
        sparkColumn = new Stack { Spacing = 3, VerticalAlignment = VerticalAlignment.Bottom }.With(sparkline, caption);
        sparkColumn.Margin = new Thickness(16, 0, 0, 2);
        var row = Ui.Columns((Ui.Star, valueBlock), (Ui.Auto, sparkColumn));
        detail = Ui.Text(12, theme.Ink2);
        promptBar = new MeterBar(theme.PromptColor);
        Children.Add(row);
        Children.Add(detail);
        Children.Add(promptBar);
    }

    public void Update(MeterViewModel model)
    {
        var phase = model.Display.DisplayPhase;
        var snapshot = model.Snapshot;
        // True when the number shown is a remembered speed rather than one being measured now.
        var historic = phase == Phase.Idle || (phase == Phase.Generating && snapshot.Phase != Phase.Generating);
        var text = phase switch
        {
            Phase.Offline => "–",
            Phase.Idle => model.Requests.FirstOrDefault() is { } last ? Format.Tps(last.GenerationTps) : "–",
            Phase.Loading => "…",
            Phase.Prefill => snapshot.PromptFraction is { } fraction ? Format.Percent(fraction) : "…",
            _ => (snapshot.LiveTps ?? model.Display.HeldTps) is { } tps ? Format.Tps(tps) : "…",
        };
        var unitText = phase switch
        {
            Phase.Prefill => "of prompt",
            Phase.Generating => "t/s",
            Phase.Idle => model.Requests.Count == 0 ? null : "t/s",
            _ => null,
        };
        if (value.Text != text) value.Text = text;
        value.Foreground = historic ? theme.Ink2 : theme.Ink;
        var unitRun = unitText is null ? "" : "  " + unitText;
        if (unit.Text != unitRun) unit.Text = unitRun;

        sparkColumn.Show(phase != Phase.Offline);
        sparkline.Values = model.TpsHistory;

        // Messages about a connection can run long. Live details stay on one line so the window holds still.
        detail.TextWrapping = phase == Phase.Offline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        detail.MaxHeight = phase == Phase.Offline ? 34 : double.PositiveInfinity;
        detail.Set(Detail(model, phase));

        // The bar's space stays reserved so the window doesn't resize when a prompt starts.
        promptBar.Visibility = phase == Phase.Offline ? Visibility.Collapsed : phase == Phase.Prefill ? Visibility.Visible : Visibility.Hidden;
        promptBar.Set(snapshot.PromptFraction ?? 0, theme.PromptColor);
    }

    private static string Detail(MeterViewModel model, Phase phase)
    {
        var snapshot = model.Snapshot;
        switch (phase)
        {
            case Phase.Offline:
                if (model.Remote is not { } remote) return "Ollama isn't running on this PC.";
                return remote.Problem ?? $"Ollama isn't running on {remote.Host}.";
            case Phase.Loading:
                return "Moving the model into memory.";
            case Phase.Idle:
                return model.Requests.FirstOrDefault() is { } last
                    ? $"Last request · {Format.Integer(last.GeneratedTokens)} tokens"
                    : "Waiting for a request.";
            case Phase.Prefill:
                {
                    var parts = new List<string>();
                    if (snapshot.PromptDone is { } done && snapshot.PromptToProcess is { } total)
                        parts.Add($"{Format.Integer(done)} of {Format.Integer(total)} new tokens");
                    if (snapshot.PromptTps is { } tps) parts.Add($"{Format.Tps(tps)} t/s");
                    if (snapshot.EtaSeconds is { } eta) parts.Add($"{Format.Duration(eta)} left");
                    return string.Join(" · ", parts);
                }
            default:
                {
                    if (snapshot.Phase != Phase.Generating) return "Between requests";
                    var parts = new List<string>();
                    if (snapshot.GeneratedTokens is { } tokens) parts.Add($"{Format.Integer(tokens)} tokens");
                    if (snapshot.PhaseElapsed is { } elapsed) parts.Add(Format.Duration(elapsed));
                    return string.Join(" · ", parts);
                }
        }
    }
}

internal static class Summaries
{
    public static string GpuSummary(MeterViewModel model)
    {
        var parts = new List<string> { model.Hardware.GpuUtilization is { } gpu ? $"{(int)gpu}%" : "–" };
        if (model.Hardware.GpuPowerWatts is { } watts) parts.Add(Format.Fixed(watts, 0, "W"));
        if (model.Hardware.DieTemperature is { } temperature) parts.Add(Format.Fixed(temperature, 0, "°C"));
        return string.Join(" · ", parts);
    }

    public static string CacheUsage(PromptCacheState cache)
    {
        var used = Format.Bytes(cache.UsedBytes);
        if (cache.LimitBytes is not { } limit || cache.Fraction is not { } fraction) return $"{used} · no limit";
        return $"{used} of {Format.Bytes(limit)} · {Format.Percent(fraction)}";
    }

    public const string CacheHelp =
        "Prompts llama-server keeps in RAM so they don't have to be read again. The prompt loaded right now isn't counted.";

    // Filling up is normal for a cache, so this stops at a warning. Dropped prompts get a notice.
    public const double CacheWarningFraction = 0.8;
}

internal sealed class CompactRowsView : Grid
{
    private readonly Theme theme;
    private readonly MeterBar gpuBar;
    private readonly TextBlock gpuValue;
    private readonly UIElement[] cacheRow;
    private readonly Path cacheWarning;
    private readonly MeterBar cacheBar;
    private readonly TextBlock cacheValue;

    public CompactRowsView(Theme theme)
    {
        this.theme = theme;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        gpuBar = new MeterBar(theme.GpuColor) { VerticalAlignment = VerticalAlignment.Center };
        gpuValue = Value();
        Place(Label("GPU"), 0, 0);
        Place(gpuBar, 0, 1);
        Place(gpuValue, 0, 2);

        cacheWarning = Glyphs.Warning(theme.Warning);
        var cacheLabel = new Stack { Orientation = Orientation.Horizontal, Spacing = 4 }.With(cacheWarning, Label("Cache"));
        cacheBar = new MeterBar(theme.Ink2Color) { VerticalAlignment = VerticalAlignment.Center, ToolTip = Summaries.CacheHelp };
        cacheValue = Value();
        cacheRow = [cacheLabel, cacheBar, cacheValue];
        Place(cacheLabel, 1, 0);
        Place(cacheBar, 1, 1);
        Place(cacheValue, 1, 2);
        foreach (var element in cacheRow) ((FrameworkElement)element).Margin = Add(((FrameworkElement)element).Margin, top: 8);
    }

    private static Thickness Add(Thickness margin, double top) => new(margin.Left, margin.Top + top, margin.Right, margin.Bottom);

    private TextBlock Label(string text)
    {
        var label = Ui.Text(11, theme.Ink2, text: text);
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private TextBlock Value()
    {
        var value = Ui.Text(12, theme.Ink, FontWeights.Medium, tabular: true);
        value.TextTrimming = TextTrimming.None;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        return value;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        SetRow(element, row);
        SetColumn(element, column);
        if (column == 1) element.Margin = new Thickness(10, 0, 10, 0);
        Children.Add(element);
    }

    public void Update(MeterViewModel model)
    {
        gpuBar.Set((model.Hardware.GpuUtilization ?? 0) / 100, theme.GpuColor);
        gpuValue.Set(Summaries.GpuSummary(model));
        var showCache = model.PromptCache is not null && model.Models.Count > 0;
        foreach (var element in cacheRow) element.Show(showCache);
        if (model.PromptCache is not { } cache || !showCache) return;
        var warn = (cache.Fraction ?? 0) >= Summaries.CacheWarningFraction;
        cacheWarning.Show(warn);
        cacheBar.Set(cache.Fraction ?? 0, warn ? ((SolidColorBrush)theme.Warning).Color : theme.Ink2Color);
        cacheValue.Set(Summaries.CacheUsage(cache));
    }
}

internal sealed class SpeedView : Grid
{
    private readonly SpeedColumn prompt;
    private readonly SpeedColumn generation;

    public SpeedView(Theme theme)
    {
        prompt = new SpeedColumn(theme, "Prompt speed", theme.PromptColor) { Margin = new Thickness(0, 0, 10, 0) };
        generation = new SpeedColumn(theme, "Generation speed", theme.GenerationColor) { Margin = new Thickness(10, 0, 0, 0) };
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        SetColumn(generation, 1);
        Children.Add(prompt);
        Children.Add(generation);
    }

    public void Update(SessionStats stats)
    {
        prompt.Update(stats.Prompt);
        generation.Update(stats.Generation);
    }
}

internal sealed class SpeedColumn : Stack
{
    private readonly Run average;
    private readonly Sparkline sparkline;
    private readonly TextBlock range;

    public SpeedColumn(Theme theme, string title, Color color)
    {
        Spacing = 4;
        average = new Run { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = theme.Ink, FontFamily = Theme.DisplayFont };
        var line = new TextBlock { FontFamily = Theme.Font };
        line.Inlines.Add(average);
        line.Inlines.Add(new Run(" t/s average") { FontSize = 11, Foreground = theme.Ink2 });
        sparkline = new Sparkline(color, theme.Hairline)
        {
            Height = 28, ToolTip = $"Speed of each of the last {SpeedStat.HistoryLimit} requests",
        };
        range = Ui.Text(11, theme.Ink2);
        Children.Add(Ui.Text(11, theme.Ink2, text: title));
        Children.Add(line);
        Children.Add(sparkline);
        Children.Add(range);
    }

    public void Update(SpeedStat stat)
    {
        var text = stat.Average is { } value ? Format.Tps(value) : "–";
        if (average.Text != text) average.Text = text;
        sparkline.Values = stat.History;
        range.Set(stat.Peak is { } peak && stat.Low is { } low ? $"Peak {Format.Tps(peak)} · Low {Format.Tps(low)}" : "No requests yet");
    }
}

internal sealed class StatTile : Stack
{
    private readonly TextBlock value;
    private readonly TextBlock note;

    public StatTile(Theme theme, string label)
    {
        Spacing = 2;
        Label = Ui.Text(11, theme.Ink2, text: label);
        value = Ui.Text(15, theme.Ink, FontWeights.SemiBold);
        note = Ui.Text(11, theme.Ink2);
        Children.Add(Label);
        Children.Add(value);
        Children.Add(note);
    }

    public TextBlock Label { get; }

    public void Update(string text, string? noteText = null)
    {
        value.Set(text);
        note.Show(noteText is not null);
        note.Set(noteText ?? "");
    }
}

internal sealed class EfficiencyView : Grid
{
    private readonly StatTile firstToken;
    private readonly StatTile cacheReuse;
    private readonly SegmentedBar split;
    private readonly TextBlock promptLegend;
    private readonly TextBlock generationLegend;
    private readonly UIElement[] splitParts;
    private readonly TextBlock none;

    public EfficiencyView(Theme theme)
    {
        firstToken = new StatTile(theme, "First token, average") { Margin = new Thickness(0, 0, 6, 0) };
        cacheReuse = new StatTile(theme, "Cache reuse") { Margin = new Thickness(6, 0, 6, 0) };
        split = new SegmentedBar(theme.PromptColor, theme.GenerationColor) { Margin = new Thickness(0, 2, 0, 2) };
        promptLegend = Legend(theme, theme.Prompt, out var promptRow);
        generationLegend = Legend(theme, theme.Generation, out var generationRow);
        none = Ui.Text(15, theme.Ink, FontWeights.SemiBold, text: "–");
        splitParts = [split, promptRow, generationRow];
        var spent = new Stack { Spacing = 4, Margin = new Thickness(6, 0, 0, 0) }
            .With(Ui.Text(11, theme.Ink2, text: "Time spent"), split, promptRow, generationRow, none);
        for (var index = 0; index < 3; index++) ColumnDefinitions.Add(new ColumnDefinition());
        SetColumn(cacheReuse, 1);
        SetColumn(spent, 2);
        Children.Add(firstToken);
        Children.Add(cacheReuse);
        Children.Add(spent);
    }

    private static TextBlock Legend(Theme theme, Brush color, out UIElement row)
    {
        var text = Ui.Text(11, theme.Ink2);
        text.VerticalAlignment = VerticalAlignment.Center;
        row = new Stack { Orientation = Orientation.Horizontal, Spacing = 5 }.With(Ui.Dot(color), text);
        return text;
    }

    public void Update(SessionStats stats)
    {
        var range = stats.FirstTokenMin is { } low && stats.FirstTokenMax is { } high
            ? $"{Format.Milliseconds(low)} to {Format.Milliseconds(high)}" : null;
        firstToken.Update(stats.FirstTokenAverage is { } average ? Format.Milliseconds(average) : "–", range);
        cacheReuse.Update(
            stats.CacheFraction is { } reuse ? Format.PercentOneDecimal(reuse) : "–",
            stats.CachedTokens > 0 ? $"{Format.CompactTokens(stats.CachedTokens)} tokens saved" : null);
        var share = stats.PromptTimeFraction;
        foreach (var part in splitParts) part.Show(share is not null);
        none.Show(share is null);
        if (share is not { } fraction) return;
        var promptPercent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
        split.Share = fraction;
        promptLegend.Set($"Prompt {promptPercent}%");
        generationLegend.Set($"Generate {100 - promptPercent}%");
    }
}

/// <summary>A labeled bar for something that fills up. It stays neutral until it nears its limit.</summary>
internal sealed class UsageBar : Stack
{
    private readonly Theme theme;
    private readonly Path warning;
    private readonly TextBlock value;
    private readonly MeterBar bar;

    public UsageBar(Theme theme, string label)
    {
        this.theme = theme;
        Spacing = 5;
        warning = Glyphs.Warning(theme.Warning);
        var title = Ui.Text(11, theme.Ink2, text: label);
        var labelRow = new Stack { Orientation = Orientation.Horizontal, Spacing = 4 }.With(warning, title);
        value = Ui.Text(11, theme.Ink2, tabular: true);
        value.TextTrimming = TextTrimming.None;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        bar = new MeterBar(theme.Ink2Color);
        Children.Add(Ui.Columns((Ui.Auto, labelRow), (Ui.Star, value)));
        Children.Add(bar);
    }

    public void Update(string text, double fraction, Brush? severity)
    {
        this.Show(true);
        warning.Show(severity is not null);
        if (severity is not null) warning.Fill = severity;
        value.Set(text);
        bar.Set(fraction, severity is SolidColorBrush brush ? brush.Color : theme.Ink2Color);
    }
}

internal sealed class PromptCacheView : Stack
{
    private readonly Theme theme;
    private readonly UsageBar bar;
    private readonly TextBlock note;

    public PromptCacheView(Theme theme)
    {
        this.theme = theme;
        Spacing = 4;
        bar = new UsageBar(theme, "Prompt cache");
        note = Ui.Text(11, theme.Ink2);
        Children.Add(bar);
        Children.Add(note);
        ToolTip = Summaries.CacheHelp;
    }

    public void Update(PromptCacheState cache)
    {
        this.Show(true);
        var fraction = cache.Fraction ?? 0;
        bar.Update(Summaries.CacheUsage(cache), fraction, fraction >= Summaries.CacheWarningFraction ? theme.Warning : null);
        if (cache.SavedPrompts <= 0)
        {
            note.Set("Nothing saved yet");
            return;
        }
        var parts = new List<string> { $"{cache.SavedPrompts} saved {(cache.SavedPrompts == 1 ? "prompt" : "prompts")}" };
        if (cache.Evictions > 0) parts.Add($"{cache.Evictions} dropped");
        if (cache.SavedTokens.Count > 0)
            parts.Add(string.Join(", ", cache.SavedTokens.Select(tokens => Format.CompactTokens(tokens))) + " tokens");
        note.Set(string.Join(" · ", parts));
    }
}

/// <summary>
/// GPU and memory readings. Each computer reports what its hardware has, so the labels follow the computer
/// whose numbers these are, not the one showing them.
/// </summary>
internal sealed class HardwareView : Stack
{
    public Theme Theme { get; }
    private readonly TextBlock gpu;
    private readonly Sparkline history;
    private readonly StatTile power, temperature, energy, gpuMemory, runnerMemory, runnerCpu, memoryTile;
    private readonly Path pressureWarning;
    private readonly TextBlock pressure;

    public HardwareView(Theme theme)
    {
        Theme = theme;
        Spacing = 10;
        gpu = Ui.Text(22, theme.Ink, FontWeights.SemiBold, display: true);
        var gpuColumn = new Stack { Width = 62 }.With(Ui.Text(11, theme.Ink2, text: "GPU"), gpu);
        history = new Sparkline(theme.GpuColor, theme.Hairline, MeterViewModel.HistoryLimit, 100)
        {
            Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
            ToolTip = "GPU load over the last 60 seconds, 0 to 100%",
        };
        gpuColumn.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Ui.Columns((Ui.Auto, gpuColumn), (Ui.Star, history)));

        power = new StatTile(theme, "GPU power");
        temperature = new StatTile(theme, "GPU temp");
        energy = new StatTile(theme, "Energy");
        pressureWarning = Glyphs.Warning(theme.Warning);
        pressure = Ui.Text(15, theme.Ink, FontWeights.SemiBold);
        var pressureTile = new Stack { Spacing = 2 }.With(
            Ui.Text(11, theme.Ink2, text: "Mem. pressure"),
            new Stack { Orientation = Orientation.Horizontal, Spacing = 4 }.With(pressureWarning, pressure));
        Children.Add(Row(power, temperature, energy, pressureTile));

        gpuMemory = new StatTile(theme, "GPU memory");
        runnerMemory = new StatTile(theme, "Runner RAM");
        runnerCpu = new StatTile(theme, "Runner CPU");
        memoryTile = new StatTile(theme, "VRAM free");
        Children.Add(Row(gpuMemory, runnerMemory, runnerCpu, memoryTile));
    }

    private static Grid Row(params FrameworkElement[] tiles)
    {
        var grid = new Grid();
        for (var index = 0; index < tiles.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            tiles[index].Margin = new Thickness(index == 0 ? 0 : 4, 0, index == tiles.Length - 1 ? 0 : 4, 0);
            Grid.SetColumn(tiles[index], index);
            grid.Children.Add(tiles[index]);
        }
        return grid;
    }

    public void Update(MeterViewModel model)
    {
        var hardware = model.Hardware;
        gpu.Set(hardware.GpuUtilization is { } load ? $"{(int)load}%" : "–");
        history.Values = model.GpuHistory;
        power.Update(hardware.GpuPowerWatts is { } watts ? Format.Fixed(watts, 1, "W") : "–");
        // A Mac reads its SoC die and shares memory with the GPU. A PC reads the GPU itself, which has its own.
        temperature.Label.Set(model.IsFromWindows ? "GPU temp" : "SoC die");
        temperature.Update(hardware.DieTemperature is { } degrees ? Format.Fixed(degrees, 0, "°C") : "–");
        energy.Update(model.JoulesPerToken is { } joules ? Format.Fixed(joules, 2, "J/t") : "–");
        var level = hardware.MemoryPressure;
        pressureWarning.Show(level is MemoryPressure.Warning or MemoryPressure.Critical);
        pressureWarning.Fill = level == MemoryPressure.Critical ? Theme.Critical : Theme.Warning;
        pressure.Set(level?.ToString() ?? "–");

        gpuMemory.Update(hardware.GpuMemoryInUse is { } used ? Format.Bytes(used) : "–");
        runnerMemory.Update(model.Runner?.ResidentBytes is { } resident ? Format.Bytes(resident) : "–");
        runnerCpu.Update(model.Runner?.CpuPercent is { } cpu ? $"{(int)cpu}%" : "–");
        if (model.IsFromWindows)
        {
            memoryTile.Label.Set("VRAM free");
            memoryTile.Update(hardware.GpuMemoryTotal is { } total && hardware.GpuMemoryInUse is { } inUse
                ? Format.Bytes(total > inUse ? total - inUse : 0) : "–");
        }
        else
        {
            memoryTile.Label.Set("Swap used");
            memoryTile.Update(hardware.SwapUsed is { } swap ? Format.Bytes(swap) : "–");
        }
    }
}

internal sealed class ModelsView : Stack
{
    private readonly Theme theme;
    private readonly Stack rows = new() { Spacing = 4 };
    private string shown = "";

    public ModelsView(Theme theme)
    {
        this.theme = theme;
        Spacing = 4;
        Children.Add(Ui.Text(11, theme.Ink2, FontWeights.SemiBold, text: "Loaded models"));
        Children.Add(rows);
    }

    public void Update(List<LoadedModel> models, DateTimeOffset now)
    {
        var lines = models.Select(model =>
        {
            var summary = Format.Bytes(model.Size);
            if (model.ExpiresAt is { } expires && expires > now) summary += $" · {Format.Countdown((expires - now).TotalSeconds)}";
            return (model.Name, summary);
        }).ToList();
        var key = string.Join("\n", lines);
        if (key == shown) return;
        shown = key;
        rows.Children.Clear();
        foreach (var (name, summary) in lines)
        {
            var value = Ui.Text(11, theme.Ink2, tabular: true, text: summary);
            value.TextTrimming = TextTrimming.None;
            value.Margin = new Thickness(8, 0, 0, 0);
            rows.Children.Add(Ui.Columns((Ui.Star, Ui.Text(12, theme.Ink, text: name)), (Ui.Auto, value)));
        }
    }
}

internal sealed class NoticesView : Stack
{
    private readonly Theme theme;
    private string shown = "";

    public NoticesView(Theme theme)
    {
        this.theme = theme;
        Spacing = 6;
    }

    public void Update(List<Notice> notices)
    {
        var key = string.Join("\n", notices.Select(notice => $"{notice.Severity}:{notice.Text}"));
        if (key == shown) return;
        shown = key;
        Children.Clear();
        foreach (var notice in notices)
        {
            var icon = Glyphs.Warning(notice.Severity == NoticeSeverity.Serious ? theme.Serious : theme.Warning);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 6, 0);
            var text = Ui.Text(12, theme.Ink, text: notice.Text);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            Children.Add(Ui.Columns((Ui.Auto, icon), (Ui.Star, text)));
        }
    }
}

internal sealed class RequestsView : Stack
{
    // The Mac's widths, with a slightly narrower gap so the seven columns fit the section.
    private static readonly double[] Widths = [30, 50, 46, 46, 62, 50, 50];
    private const double Gap = 5;

    private readonly Theme theme;
    private readonly Stack rows = new() { Spacing = 4 };
    private string shown = "";

    public RequestsView(Theme theme)
    {
        this.theme = theme;
        Spacing = 4;
        Children.Add(Ui.Text(11, theme.Ink2, FontWeights.SemiBold, text: "Recent requests"));
        Children.Add(Row(["Age", "Prompt", "Cache", "Output", "Prompt t/s", "Gen t/s", "Total"], theme.Ink2));
        Children.Add(Ui.Hairline(theme));
        Children.Add(rows);
    }

    private static Stack Row(string[] cells, Brush brush)
    {
        var row = new Stack { Orientation = Orientation.Horizontal, Spacing = Gap };
        for (var index = 0; index < cells.Length; index++)
        {
            var cell = Ui.Text(11, brush, tabular: true, text: cells[index]);
            cell.Width = Widths[index];
            cell.TextAlignment = index == 0 ? TextAlignment.Left : TextAlignment.Right;
            row.Children.Add(cell);
        }
        return row;
    }

    public void Update(List<CompletedRequest> requests, DateTimeOffset now)
    {
        var lines = requests.Select(request => new[]
        {
            request.FinishedAt is { } finished ? Format.Age((now - finished).TotalSeconds) : "–",
            Format.Integer(request.PromptTokens),
            Format.Percent(request.CacheFraction),
            Format.Integer(request.GeneratedTokens),
            Format.Tps(request.PromptTps),
            Format.Tps(request.GenerationTps),
            Format.Milliseconds(request.TotalMilliseconds),
        }).ToList();
        var key = string.Join("\n", lines.Select(cells => string.Join("|", cells)));
        if (key == shown) return;
        shown = key;
        rows.Children.Clear();
        foreach (var cells in lines) rows.Children.Add(Row(cells, theme.Ink));
    }
}
