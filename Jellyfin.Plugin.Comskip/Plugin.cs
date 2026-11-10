using System.Globalization;
using Jellyfin.Plugin.Comskip.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Comskip;

/// <summary>
/// Comskip commercial detection plugin. Runs comskip against recordings and library videos
/// and exposes the detected commercial breaks to Jellyfin as "Commercial" media segments,
/// so clients can offer "Skip commercial" exactly like Intro Skipper's "Skip intro".
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable plugin id. Never change this once released.</summary>
    public const string PluginGuid = "bc780563-77ca-4c84-bb79-8993c65bd271";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Results live next to the plugin's XML config so they survive plugin updates
        // (the plugin install folder is replaced on every update).
        StoragePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "Comskip");
        WorkPath = Path.Combine(applicationPaths.CachePath, "comskip");
        Directory.CreateDirectory(StoragePath);
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Comskip";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string Description =>
        "Detects commercials in recordings with comskip and exposes them as skippable Commercial segments.";

    /// <summary>Gets the folder used for persistent analysis results.</summary>
    public string StoragePath { get; }

    /// <summary>Gets the folder used for temporary comskip working files.</summary>
    public string WorkPath { get; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        ];
    }
}
