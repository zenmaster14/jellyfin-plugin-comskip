using Jellyfin.Plugin.Comskip.Cutting;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Configuration;
using Jellyfin.Plugin.Comskip.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Comskip.Services;

/// <summary>
/// Watches the library: new or changed videos in scope are queued for analysis (this is how
/// fresh DVR recordings get processed automatically), and removed items have their stored
/// results deleted.
/// </summary>
public sealed class LibraryWatcher : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ItemEligibility _eligibility;
    private readonly AnalysisQueue _queue;
    private readonly ResultStore _store;
    private readonly CutLedger _ledger;
    private readonly ITaskManager _taskManager;
    private bool _removeCommercials;

    /// <summary>Initializes a new instance of the <see cref="LibraryWatcher"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="queue">Analysis queue.</param>
    /// <param name="store">Result store.</param>
    /// <param name="ledger">Cut ledger.</param>
    /// <param name="taskManager">Scheduled task manager.</param>
    public LibraryWatcher(
        ILibraryManager libraryManager,
        ItemEligibility eligibility,
        AnalysisQueue queue,
        ResultStore store,
        CutLedger ledger,
        ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _eligibility = eligibility;
        _queue = queue;
        _store = store;
        _ledger = ledger;
        _taskManager = taskManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        if (Plugin.Instance is not null)
        {
            _removeCommercials = Plugin.Instance.Configuration.RemoveCommercials;
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoAnalyzeNewItems || !_eligibility.IsEligible(e.Item))
        {
            return;
        }

        if (ResultStore.Matches(_store.Get(e.Item.Id), e.Item.Path))
        {
            return;
        }

        // Give the file time to finish writing before the first attempt.
        _queue.Enqueue(e.Item.Id, TimeSpan.FromSeconds(Math.Max(30, config.SettleSeconds)));
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not null)
        {
            _store.Delete(e.Item.Id);
            _ledger.Remove(e.Item.Id);
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        _eligibility.Invalidate();

        // Turning commercial removal on applies it to every recording already analyzed, right away
        // rather than at the next nightly run.
        var enabled = e is PluginConfiguration config && config.RemoveCommercials;
        if (enabled && !_removeCommercials)
        {
            _taskManager.QueueScheduledTask<DetectCommercialsTask>();
        }

        _removeCommercials = enabled;
    }
}
