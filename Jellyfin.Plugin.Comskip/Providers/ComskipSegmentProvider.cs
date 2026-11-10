using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.Comskip.Providers;

/// <summary>
/// Exposes stored comskip results as Jellyfin "Commercial" media segments. Jellyfin clients then
/// show "Skip commercial" or skip automatically, depending on each user's Playback settings.
/// Analysis never runs inline here (comskip takes minutes); the library watcher and the
/// scheduled task produce the results this provider reads.
/// </summary>
public class ComskipSegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly ItemEligibility _eligibility;
    private readonly ResultStore _store;

    /// <summary>Initializes a new instance of the <see cref="ComskipSegmentProvider"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="store">Result store.</param>
    public ComskipSegmentProvider(ILibraryManager libraryManager, ItemEligibility eligibility, ResultStore store)
    {
        _libraryManager = libraryManager;
        _eligibility = eligibility;
        _store = store;
    }

    /// <inheritdoc />
    public string Name => "Comskip";

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        var result = _store.Get(request.ItemId);

        if (item is null || result is null || result.Status != AnalysisStatus.Success || !ResultStore.Matches(result, item.Path))
        {
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);
        }

        IReadOnlyList<MediaSegmentDto> segments = result.Breaks
            .Select(b => new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = request.ItemId,
                Type = MediaSegmentType.Commercial,
                StartTicks = TimeSpan.FromSeconds(b.Start).Ticks,
                EndTicks = TimeSpan.FromSeconds(b.End).Ticks
            })
            .ToList();

        return Task.FromResult(segments);
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(_eligibility.IsEligible(item));

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _store.Delete(itemId);
        return Task.CompletedTask;
    }
}
