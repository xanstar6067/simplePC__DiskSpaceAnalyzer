using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DiskSpaceAnalyzer.Services;

[McpServerToolType]
public sealed class AnalysisMcpTools(AnalysisQueryService queries)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "get_analysis_status", ReadOnly = true), Description(
        "Get current application/scan status, selected path and saved snapshot descriptor. Call first to obtain analysisId. A running scan is not yet published.")]
    public Task<string> GetAnalysisStatus(CancellationToken cancellationToken) =>
        Execute(() => queries.GetStatusAsync(cancellationToken));

    [McpServerTool(Name = "get_summary", ReadOnly = true), Description(
        "Get saved scan totals and skipped/inaccessible node counts. Sizes of folders include descendants. Risk classifications do not establish whether files can be deleted.")]
    public Task<string> GetSummary(string analysisId, CancellationToken cancellationToken) =>
        Execute(() => queries.GetSummaryAsync(analysisId, cancellationToken));

    [McpServerTool(Name = "list_children", ReadOnly = true), Description(
        "List immediate children of a scanned folder, including collapsed/unloaded UI nodes. sortBy: disk, logical, name, modified. limit 1–200. Continue with nextOffset.")]
    public Task<string> ListChildren(string analysisId, CancellationToken cancellationToken,
        string? path = null, string sortBy = "disk", int offset = 0, int limit = 50) =>
        Execute(() => queries.GetNodesAsync(analysisId, path, true, "all", sortBy, null, 0, null, null, offset, limit, cancellationToken));

    [McpServerTool(Name = "find_largest", ReadOnly = true), Description(
        "Find largest descendants in a saved subtree. kind: file, folder, all. sortBy: disk or logical. Folders overlap; do not sum their sizes. Defaults to files.")]
    public Task<string> FindLargest(string analysisId, CancellationToken cancellationToken,
        string? path = null, string kind = "file", string sortBy = "disk", int offset = 0, int limit = 50) =>
        Execute(() => queries.GetNodesAsync(analysisId, path, false, kind, sortBy, null, 0, null, null, offset, limit, cancellationToken));

    [McpServerTool(Name = "search_nodes", ReadOnly = true), Description(
        "Search saved descendants by literal case-insensitive path/name substring, logical byte range and modification date. kind: all, file, folder. modifiedBefore is ISO 8601. No file contents are read.")]
    public Task<string> SearchNodes(string analysisId, CancellationToken cancellationToken,
        string? query = null, string? path = null, string kind = "all", long minBytes = 0,
        long? maxBytes = null, string? modifiedBefore = null, int offset = 0, int limit = 50) =>
        Execute(() => queries.GetNodesAsync(analysisId, path, false, kind, "disk", query, minBytes, maxBytes, modifiedBefore, offset, limit, cancellationToken));

    [McpServerTool(Name = "group_by_extension", ReadOnly = true), Description(
        "Aggregate files in a saved subtree by lowercase extension, sorted by size on disk. Empty extension means no extension. Counts files only, avoiding overlapping folder totals.")]
    public Task<string> GroupByExtension(string analysisId, CancellationToken cancellationToken,
        string? path = null, int offset = 0, int limit = 50) =>
        Execute(() => queries.GroupByExtensionAsync(analysisId, path, offset, limit, cancellationToken));

    [McpServerTool(Name = "reveal_node", ReadOnly = false, Destructive = false), Description(
        "Navigate to a scanned path in the application's window. Changes UI selection only; does not open, modify or delete files.")]
    public Task<string> RevealNode(string analysisId, string path, CancellationToken cancellationToken) =>
        Execute(() => queries.RevealAsync(analysisId, path, cancellationToken));

    private static async Task<string> Execute<T>(Func<Task<T>> action)
    {
        try { return JsonSerializer.Serialize(await action(), JsonOptions); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new McpException(exception.Message);
        }
        catch (Exception exception) when (exception is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new McpException("Снимок анализа недоступен. Обновите состояние через get_analysis_status или повторите сканирование в приложении.");
        }
    }
}
