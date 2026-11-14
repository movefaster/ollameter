using Ollameter.Core;

namespace Ollameter;

/// <summary>
/// Chooses whose numbers the window and tray icon show, one computer or several, Macs and PCs alike, and shares
/// this PC's when that's turned on. This PC keeps being measured while others are shown, so sharing carries on.
/// </summary>
internal sealed class MeterHub
{
    private readonly Settings settings = Settings.Current;
    private readonly Dictionary<MeterSource, RemoteClient> clients = [];
    /// <summary>The computer the tray icon follows: the busiest one.</summary>
    private MeterSource? primarySource;
    private bool panelVisible = true;

    /// <summary>One model per watched computer, in the order of <see cref="Sources"/>.</summary>
    public List<MeterViewModel> Models { get; private set; } = [];
    public List<MeterSource> Sources { get; private set; }
    public bool IsSharing { get; private set; }

    public Monitor Monitor { get; }
    public ShareServer Server { get; } = new();
    public Dnssd.Browser Browser { get; } = new();

    /// <summary>Runs on every update shown, whether or not the window is open.</summary>
    public event Action? Updated;
    /// <summary>Runs when the computers found nearby or the state of sharing change.</summary>
    public event Action? NetworkChanged;

    public bool IsPanelVisible
    {
        get => panelVisible;
        set
        {
            panelVisible = value;
            UpdateDetail();
        }
    }

    /// <summary>Overrides apply to this run only and aren't saved.</summary>
    public MeterHub(Monitor monitor, List<MeterSource>? sourcesOverride = null, bool? sharingOverride = null)
    {
        Monitor = monitor;
        Sources = MeterSource.Ordered(sourcesOverride ?? SavedSources());
        IsSharing = sharingOverride ?? settings.Sharing;
    }

    private List<MeterSource> SavedSources()
    {
        var saved = settings.Sources?.Select(MeterSource.FromStorageValue).OfType<MeterSource>().ToList() ?? [];
        return saved.Count > 0 ? saved : [MeterSource.Local];
    }

    public void Start()
    {
        Monitor.Updated += LocalUpdated;
        Server.Changed += () =>
        {
            UpdateDetail();
            NetworkChanged?.Invoke();
        };
        Browser.Changed += () => NetworkChanged?.Invoke();
        Marks.Add("hub start");
        Monitor.Start();
        Marks.Add("monitor started");
        if (IsSharing) Server.Start();
        // Browsing needs no permission on Windows, so it starts at once and the menu is ready when opened.
        Browser.Start();
        Marks.Add("browser started");
        Apply(Sources);
        Marks.Add("sources applied");
    }

    public void Stop()
    {
        Monitor.Stop();
        foreach (var client in clients.Values) client.Stop();
        Server.Stop();
        Browser.Dispose();
    }

    /// <summary>What the tray icon shows.</summary>
    public MeterViewModel Primary
    {
        get
        {
            if (primarySource is not null && Sources.IndexOf(primarySource) is var index and >= 0 && index < Models.Count)
                return Models[index];
            return Models.FirstOrDefault() ?? Monitor.Model;
        }
    }

    /// <summary>
    /// Other computers found sharing, without this one. Its own name also lingers for a moment after sharing stops,
    /// so that's left out too.
    /// </summary>
    public List<string> Peers =>
        Browser.Names
            .Where(name => !string.Equals(name, Server.AdvertisedName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, Sharing.HostName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>A line for the menu about how other computers reach this one, when sharing is on.</summary>
    public string? SharingStatus
    {
        get
        {
            if (!IsSharing) return null;
            if (!Server.IsRunning) return "Couldn't start sharing";
            if (Server.Port is not { } port) return "Starting…";
            return $"Others see this PC as {Server.AdvertisedName ?? Sharing.HostName}, port {port}";
        }
    }

    /// <summary>Adds a computer, or removes it. The last one stays, so the window always shows something.</summary>
    public void Toggle(MeterSource source)
    {
        if (Sources.Contains(source))
        {
            if (Sources.Count <= 1) return;
            Save(Sources.Where(other => other != source));
        }
        else
        {
            Save(Sources.Append(source));
        }
    }

    public void Add(MeterSource source)
    {
        if (!Sources.Contains(source)) Save(Sources.Append(source));
    }

    public void SetSharing(bool on, bool persist = true)
    {
        if (persist)
        {
            settings.Sharing = on;
            settings.Save();
        }
        if (on == IsSharing) return;
        IsSharing = on;
        if (on)
        {
            Server.Publish(Monitor.Model);
            Server.Start();
        }
        else
        {
            Server.Stop();
        }
        UpdateDetail();
        NetworkChanged?.Invoke();
    }

    /// <summary>Only this PC's models can be unloaded from here. Sharing is read-only.</summary>
    public void PressUnload(string name)
    {
        if (Sources.Contains(MeterSource.Local)) Monitor.PressUnload(name);
    }

    private void Save(IEnumerable<MeterSource> list)
    {
        var ordered = MeterSource.Ordered(list);
        settings.Sources = ordered.Select(source => source.StorageValue).ToList();
        settings.Save();
        Apply(ordered);
    }

    /// <summary>Starts following new computers and stops following removed ones. Ones that stay keep their numbers.</summary>
    private void Apply(List<MeterSource> list)
    {
        var kept = new Dictionary<MeterSource, MeterViewModel>();
        for (var index = 0; index < Math.Min(Sources.Count, Models.Count); index++) kept[Sources[index]] = Models[index];
        foreach (var (source, client) in clients.Where(pair => !list.Contains(pair.Key)).ToList())
        {
            client.Stop();
            clients.Remove(source);
        }
        Sources = list;
        Models = list.Select(source =>
        {
            if (kept.TryGetValue(source, out var model)) return model;
            if (source.IsLocal) return Monitor.Model;
            return MeterViewModels.Placeholder(new RemoteSource(source.DisplayName, RemoteStatus.Connecting));
        }).ToList();
        // Clients report as soon as they start, so the models above have to be in place first.
        foreach (var source in list.Where(source => !source.IsLocal && !clients.ContainsKey(source)))
        {
            var client = new RemoteClient(source);
            client.ModelChanged += model =>
            {
                if (clients.TryGetValue(source, out var current) && current == client) Show(model, source);
            };
            clients[source] = client;
            client.Start();
        }
        ChoosePrimary();
        UpdateDetail();
        Updated?.Invoke();
    }

    private void LocalUpdated(MeterViewModel model)
    {
        if (IsSharing) Server.Publish(model);
        if (Sources.Contains(MeterSource.Local)) Show(model, MeterSource.Local);
    }

    private void Show(MeterViewModel model, MeterSource source)
    {
        var index = Sources.IndexOf(source);
        if (index < 0 || index >= Models.Count) return;
        Models[index] = model;
        ChoosePrimary();
        Updated?.Invoke();
    }

    private void ChoosePrimary()
    {
        int? current = primarySource is null ? null : Sources.IndexOf(primarySource) is var index and >= 0 ? index : null;
        primarySource = MeterViewModels.BusiestIndex(Models, current) is { } busiest ? Sources[busiest] : null;
    }

    /// <summary>Power, thermal and process readings are worth taking while someone can see them.</summary>
    private void UpdateDetail() =>
        Monitor.WantsDetail = (IsPanelVisible && Sources.Contains(MeterSource.Local)) || Server.HasViewers;
}
