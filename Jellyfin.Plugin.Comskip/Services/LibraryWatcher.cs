using Jellyfin.Plugin.Comskip.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
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

    /// <summary>Initializes a new instance of the <see cref="LibraryWatcher"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="queue">Analysis queue.</param>
    /// <param name="store">Result store.</param>
    public LibraryWatcher(ILibraryManager libraryManager, ItemEligibility eligibility, AnalysisQueue queue, ResultStore store)
    {
        _libraryManager = libraryManager;
        _eligibility = eligibility;
        _queue = queue;
        _store = store;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        if (Plugin.Instance is not null)
        {
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
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => _eligibility.Invalidate();
}
