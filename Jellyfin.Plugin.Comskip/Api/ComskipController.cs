using System.Net.Mime;
using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Comskip.Api;

/// <summary>Admin endpoints used by the settings page.</summary>
[ApiController]
[Route("Comskip")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ComskipController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ToolLocator _tools;
    private readonly AnalysisQueue _queue;
    private readonly CommercialAnalyzer _analyzer;
    private readonly ResultStore _store;
    private readonly ItemEligibility _eligibility;

    /// <summary>Initializes a new instance of the <see cref="ComskipController"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="tools">Tool locator.</param>
    /// <param name="queue">Queue.</param>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="store">Result store.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    public ComskipController(
        ILibraryManager libraryManager,
        ToolLocator tools,
        AnalysisQueue queue,
        CommercialAnalyzer analyzer,
        ResultStore store,
        ItemEligibility eligibility)
    {
        _libraryManager = libraryManager;
        _tools = tools;
        _queue = queue;
        _analyzer = analyzer;
        _store = store;
        _eligibility = eligibility;
    }

    /// <summary>Gets tool detection and queue status.</summary>
    /// <returns>Status object.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStatus()
    {
        var (libraryIds, roots) = _eligibility.GetScope();
        return Ok(new
        {
            ComskipPath = _tools.FindComskip(),
            FfmpegPath = _tools.FindFfmpeg(),
            Queued = _queue.PendingCount,
            Running = _analyzer.Running.Values.ToArray(),
            AnalyzedItems = _store.Count(),
            LibrariesInScope = libraryIds.Length,
            ScopeFolders = roots,
            Recent = _queue.Recent
        });
    }

    /// <summary>Gets the stored result for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The result.</returns>
    [HttpGet("Results/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AnalysisResult> GetResult([FromRoute] Guid itemId)
    {
        var result = _store.Get(itemId);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }

    /// <summary>Queues an item (or, for a series/season/folder, all videos below it) for analysis.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="force">Re-analyze even if a current result exists.</param>
    /// <returns>Number of items queued.</returns>
    [HttpPost("Analyze/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Analyze([FromRoute] Guid itemId, [FromQuery] bool force = true)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var targets = new List<Guid>();
        if (item is MediaBrowser.Controller.Entities.Folder folder)
        {
            targets.AddRange(folder.GetRecursiveChildren(i => _eligibility.IsEligible(i)).Select(i => i.Id));
        }
        else if (_eligibility.IsEligible(item))
        {
            targets.Add(item.Id);
        }

        foreach (var id in targets)
        {
            _queue.Enqueue(id, TimeSpan.Zero, force);
        }

        return Ok(new { Queued = targets.Count });
    }

    /// <summary>Deletes the stored result for an item and removes its commercial segments.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Results/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteResult([FromRoute] Guid itemId)
    {
        _store.Delete(itemId);
        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            await _analyzer.RefreshSegmentsAsync(item, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        return NoContent();
    }
}
