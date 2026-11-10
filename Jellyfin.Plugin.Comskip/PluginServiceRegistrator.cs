using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Providers;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Comskip;

/// <summary>Registers the plugin's services with Jellyfin's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ToolLocator>();
        serviceCollection.AddSingleton<ComskipRunner>();
        serviceCollection.AddSingleton<ResultStore>();
        serviceCollection.AddSingleton<ItemEligibility>();
        serviceCollection.AddSingleton<CommercialAnalyzer>();

        serviceCollection.AddSingleton<AnalysisQueue>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AnalysisQueue>());
        serviceCollection.AddHostedService<LibraryWatcher>();

        serviceCollection.AddSingleton<IMediaSegmentProvider, ComskipSegmentProvider>();
    }
}
