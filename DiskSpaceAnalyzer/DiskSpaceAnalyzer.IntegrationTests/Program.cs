using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.ViewModels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskSpaceAnalyzer.Views;

// An executable integration suite: no test framework or external services required.
internal static class Program
{
    private static int _checks;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var temporary = Path.Combine(Path.GetTempPath(), "simplepc-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var rootPath = Path.Combine(temporary, "scan");
            var nested = Path.Combine(rootPath, "данные");
            var skipped = Path.Combine(rootPath, "cache");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(skipped);
            await File.WriteAllBytesAsync(Path.Combine(rootPath, "large.BIN"), new byte[8192], token);
            await File.WriteAllBytesAsync(Path.Combine(rootPath, "notes.txt"), new byte[1024], token);
            await File.WriteAllBytesAsync(Path.Combine(rootPath, "README"), new byte[17], token);
            await File.WriteAllBytesAsync(Path.Combine(nested, "архив.bin"), new byte[4096], token);
            await File.WriteAllBytesAsync(Path.Combine(nested, "100%_literal.txt"), new byte[7], token);
            await File.WriteAllBytesAsync(Path.Combine(skipped, "hidden.bin"), new byte[16384], token);

            AnalysisContext context = new(null, null, false, "", "No analysis", []);
            string? revealed = null;
            var queries = new AnalysisQueryService(_ => Task.FromResult(context), (path, _) =>
            {
                revealed = path;
                return Task.FromResult(true);
            });
            var empty = Json( await queries.GetStatusAsync(token));
            Check(empty.GetProperty("snapshot").ValueKind == JsonValueKind.Null, "No saved snapshot is reported explicitly");

            var cache = new AnalysisCacheService(Path.Combine(temporary, "cache-db"));
            var scanner = new DiskScanner(new PathRiskClassifier(), cache);
            var root = await scanner.ScanAsync(rootPath, new ScanOptions
            {
                IgnoreCache = true, SizeCalculationMode = SizeCalculationMode.Logical, ExcludedPaths = [skipped]
            }, null, token);
            Check(root.IsCacheBacked && root.HasUnloadedCachedChildren, "Real scan publishes a lazy SQLite snapshot");
            context = new(root.CacheDatabasePath, nested, false, "", "Done", []);
            var status = Json(await queries.GetStatusAsync(token));
            var id = status.GetProperty("snapshot").GetProperty("analysisId").GetString()!;
            Check(status.GetProperty("snapshot").GetProperty("sizeCalculationMode").GetString() == "Logical", "Measurement mode comes from snapshot metadata");
            var summary = Json(await queries.GetSummaryAsync(id, token));
            Check(summary.GetProperty("root").GetProperty("logicalBytes").GetInt64() == 13336, "Summary matches actual scanned files, excluding skipped subtree");
            Check(summary.GetProperty("omittedNodes").GetProperty("Skipped").GetInt64() == 1, "Skipped regions are reported");

            var children = await queries.GetNodesAsync(id, null, true, "all", "logical", null, 0, null, null, 0, 2, token);
            Check(children.Items.Count == 2 && children.Items[0].Name == "large.BIN" && children.NextOffset == 2, "Direct child ordering and pagination");
            var second = await queries.GetNodesAsync(id, null, true, "all", "logical", null, 0, null, null, 2, 200, token);
            Check(!children.Items.Select(n => n.Id).Intersect(second.Items.Select(n => n.Id)).Any() && second.NextOffset is null, "Pagination has no duplicates and terminates");
            var largest = await queries.GetNodesAsync(id, null, false, "file", "logical", null, 0, null, null, 0, 200, token);
            Check(largest.Items.Count == 5 && largest.Items[1].Name == "архив.bin", "Largest files include unloaded nested Unicode paths");
            var literal = await queries.GetNodesAsync(id, null, false, "file", "disk", "%_", 0, null, null, 0, 50, token);
            Check(literal.Items.Single().Name == "100%_literal.txt", "Search treats SQL wildcard characters literally");
            var injection = await queries.GetNodesAsync(id, null, false, "all", "disk", "' OR 1=1 --", 0, null, null, 0, 50, token);
            Check(injection.Items.Count == 0, "Search input cannot inject SQL");
            var filtered = await queries.GetNodesAsync(id, nested.ToUpperInvariant(), false, "file", "logical", "АРХИВ", 1000, 5000, DateTimeOffset.UtcNow.AddDays(1).ToString("O"), 0, 50, token);
            Check(filtered.Items.Single().LogicalBytes == 4096, "Unicode case-insensitive scope/search, size and date filters");
            var groups = await queries.GroupByExtensionAsync(id, null, 0, 50, token);
            Check(groups.Items.Single(g => g.Extension == ".bin").LogicalBytes == 12288, "Extension aggregation counts files once across nested directories");
            Check(groups.Items.Sum(g => g.LogicalBytes) == 13336 && groups.Items.Any(g => g.Extension == ""), "Aggregate matches total, including files without extensions");
            Check(root.HasUnloadedCachedChildren, "Queries never expand the WPF tree");
            await ThrowsAsync(() => queries.GetSummaryAsync("obsolete-id", token), "Stale snapshot id is rejected");
            await ThrowsAsync(() => queries.GetNodesAsync(id, temporary, true, "all", "disk", null, 0, null, null, 0, 50, token), "Scope outside snapshot is rejected");
            await ThrowsAsync(() => queries.GetNodesAsync(id, null, false, "all", "disk", null, 0, null, null, 0, 201, token), "Response limits are enforced");
            context = context with { ExcludedPaths = [nested] };
            var excluded = await queries.GetNodesAsync(id, null, false, "all", "disk", null, 0, null, null, 0, 50, token);
            Check(excluded.Items.All(n => !PathRiskClassifier.IsSameOrChild(n.Path, nested)), "UI exclusions hide entire subtrees");
            await ThrowsAsync(() => queries.GroupByExtensionAsync(id, nested, 0, 50, token), "An excluded scope is rejected");
            context = context with { ExcludedPaths = [], IsScanning = true };
            Check(Json(await queries.GetStatusAsync(token)).GetProperty("isScanning").GetBoolean(), "Running scan is distinct from saved snapshot");
            Check((await queries.GroupByExtensionAsync(id, null, 0, 50, token)).Items.Sum(g => g.FileCount) == 5, "Saved snapshot remains queryable while a new scan runs");
            context = context with { IsScanning = false };
            await queries.RevealAsync(id, Path.Combine(nested, "архив.bin"), token);
            Check(revealed == Path.Combine(nested, "архив.bin"), "Reveal dispatches only a validated snapshot path");

            await TestHttpAsync(queries, id, rootPath, token);
            await TestWpfAsync(root, nested, token);

            // A replacement scan must invalidate identifiers held by an agent.
            await File.WriteAllBytesAsync(Path.Combine(rootPath, "new.txt"), new byte[33], token);
            root = await scanner.ScanAsync(rootPath, new ScanOptions
            {
                IgnoreCache = true, SizeCalculationMode = SizeCalculationMode.Logical, ExcludedPaths = [skipped]
            }, null, token);
            context = context with { DatabasePath = root.CacheDatabasePath };
            await ThrowsAsync(() => queries.GetSummaryAsync(id, token), "Replacing the database invalidates old analysis ids");
            context = context with { DatabasePath = null };
            await ThrowsAsync(() => queries.GetSummaryAsync(id, token), "Clearing analysis does not expose an old database");
            Console.WriteLine($"PASS: {_checks} integration checks.");
        }
        finally
        {
            // Only the unique test directory created above is removed.
            if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task TestHttpAsync(AnalysisQueryService queries, string id, string root, CancellationToken token)
    {
        var port = FreePort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = new AnalysisMcpServer(queries);
        await server.StartAsync(port, secret, token);
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var unauthenticated = await http.GetAsync(endpoint, token);
        Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "HTTP requires a bearer token");
        using var wrongToken = new HttpRequestMessage(HttpMethod.Get, endpoint);
        wrongToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        Check((await http.SendAsync(wrongToken, token)).StatusCode == HttpStatusCode.Unauthorized, "Invalid token is rejected");
        using var origin = new HttpRequestMessage(HttpMethod.Get, endpoint);
        origin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        origin.Headers.Add("Origin", "https://untrusted.example");
        Check((await http.SendAsync(origin, token)).StatusCode == HttpStatusCode.Forbidden, "Untrusted browser origin is rejected even with token");
        using var host = new HttpRequestMessage(HttpMethod.Get, endpoint);
        host.Headers.Host = "untrusted.example:" + port;
        host.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Check((await http.SendAsync(host, token)).StatusCode == HttpStatusCode.Forbidden, "DNS-rebinding Host is rejected");

        // Real official MCP client: discovery/handshake, tools/list, tools/call and protocol errors.
        await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
        }), cancellationToken: token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            Check(tools.Count == 7, "MCP client discovers all seven tools");
            var status = await CallAsync(client, "get_analysis_status", new(), token);
            Check(status.GetProperty("snapshot").GetProperty("analysisId").GetString() == id, "MCP status returns the active saved snapshot");
            var summary = await CallAsync(client, "get_summary", new() { ["analysisId"] = id }, token);
            Check(summary.GetProperty("root").GetProperty("logicalBytes").GetInt64() == 13336, "MCP summary returns actual scan totals");
            var children = await CallAsync(client, "list_children", new() { ["analysisId"] = id, ["limit"] = 1 }, token);
            Check(children.GetProperty("items").GetArrayLength() == 1 && children.GetProperty("nextOffset").GetInt32() == 1, "MCP optional arguments and pagination work");
            var largest = await CallAsync(client, "find_largest", new() { ["analysisId"] = id, ["sortBy"] = "logical" }, token);
            Check(largest.GetProperty("items")[0].GetProperty("name").GetString() == "large.BIN", "MCP largest-file query works");
            var search = await CallAsync(client, "search_nodes", new() { ["analysisId"] = id, ["query"] = "АРХИВ" }, token);
            Check(search.GetProperty("items").GetArrayLength() == 1, "MCP Unicode search works");
            var extensions = await CallAsync(client, "group_by_extension", new() { ["analysisId"] = id }, token);
            Check(extensions.GetProperty("items").GetArrayLength() == 3, "MCP extension aggregation works");
            var reveal = await CallAsync(client, "reveal_node", new() { ["analysisId"] = id, ["path"] = root }, token);
            Check(reveal.GetProperty("revealed").GetBoolean(), "MCP reveal dispatches navigation");
            var invalid = await client.CallToolAsync("get_summary", new Dictionary<string, object?> { ["analysisId"] = "stale" }, cancellationToken: token);
            Check(invalid.IsError == true && invalid.Content.OfType<TextContentBlock>().Any(c => c.Text.Contains("get_analysis_status")), "Tool errors explain how to recover a stale id");
        }

        await server.StopAsync();
        Check(!server.IsRunning, "Server shuts down cleanly");
        using (var listener = new TcpListener(IPAddress.Loopback, port))
        {
            listener.Start();
            Check(true, "Shutdown releases the listening port");
        }
        await server.StartAsync(port, secret, token);
        Check(server.IsRunning, "Server can restart after being disabled");
        await using var conflict = new AnalysisMcpServer(queries);
        await ThrowsAsync(() => conflict.StartAsync(port, secret, token), "Port conflict fails without stopping the existing server");
    }

    private static Task TestWpfAsync(ScanNode root, string nested, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var model = new MainViewModel(loadDrives: false);
                    model.Roots.Add(root);
                    model.SelectedNode = root;
                    var file = Path.Combine(nested, "архив.bin");
                    Check(model.RevealAnalysisNode(file) && model.SelectedFlatNode?.FullPath == file && model.SelectedNode?.Parent?.IsExpanded == true,
                        "WPF navigation loads ancestors and selects the requested file");
                    Check(!model.RevealAnalysisNode(Path.GetDirectoryName(root.FullPath)!), "WPF navigation refuses paths outside the active analysis");
                    Check(model.CaptureAnalysisContext().DatabasePath == root.CacheDatabasePath, "Dispatcher bridge publishes only saved snapshot metadata");
                    model.ExcludeNodeCommand.Execute(model.SelectedNode?.Parent);
                    Check(!model.RevealAnalysisNode(file), "WPF navigation respects current UI exclusions");

                    var query = new AnalysisQueryService(_ => Task.FromResult(new AnalysisContext(null, null, false, "", "", [])), (_, _) => Task.FromResult(false));
                    var server = new AnalysisMcpServer(query);
                    var settings = new McpServerSettings { Port = FreePort() };
                    McpServerSettings? saved = null;
                    await using var connection = new McpConnectionViewModel(server, settings, value => saved = value);
                    Check(!connection.CopyPromptCommand.CanExecute(null), "Copy button is disabled until server startup succeeds");
                    await connection.InitializeAsync();
                    Check(server.IsRunning && connection.CopyPromptCommand.CanExecute(null) && saved?.Enabled == true,
                        "Connection view model starts server and enables copy button");
                    var prompt = connection.BuildPrompt();
                    Check(prompt.Contains($"http://127.0.0.1:{settings.Port}/mcp") && prompt.Contains(settings.Token) && prompt.Contains("get_analysis_status"),
                        "Generated prompt contains the running server's URL, key and workflow");
                    var oldClipboard = System.Windows.Clipboard.GetDataObject();
                    try
                    {
                        connection.CopyPromptCommand.Execute(null);
                        Check(System.Windows.Clipboard.GetText() == prompt && connection.Feedback.Contains("скопирован"),
                            "Copy command writes the complete prompt to the Windows clipboard");
                    }
                    finally
                    {
                        if (oldClipboard is not null) System.Windows.Clipboard.SetDataObject(oldClipboard, true);
                        else System.Windows.Clipboard.Clear();
                    }

                    var renderDirectory = Path.Combine(AppContext.BaseDirectory, "ui-check");
                    Directory.CreateDirectory(renderDirectory);
                    foreach (var theme in new[] { "Light", "Dark" })
                    {
                        app.Resources.MergedDictionaries.Clear();
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/DiskSpaceAnalyzer;component/Themes/{theme}Theme.xaml") });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DiskSpaceAnalyzer;component/Themes/Controls.xaml") });
                        var window = new McpConnectionWindow(connection);
                        var content = (FrameworkElement)window.Content;
                        content.Measure(new System.Windows.Size(624, double.PositiveInfinity));
                        content.Arrange(new Rect(new System.Windows.Size(624, content.DesiredSize.Height)));
                        content.UpdateLayout();
                        Check(content.ActualHeight < 680 && content.ActualHeight > 350, $"{theme} connection dialog fits a standard desktop height");
                        var visual = new DrawingVisual();
                        using (var drawing = visual.RenderOpen())
                        {
                            drawing.DrawRectangle((Brush)app.FindResource("PanelBackgroundBrush"), null, new Rect(0, 0, 624, content.ActualHeight));
                            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, 624, content.ActualHeight));
                        }
                        var bitmap = new RenderTargetBitmap(624, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(renderDirectory, $"mcp-{theme.ToLowerInvariant()}.png"));
                        encoder.Save(output);
                        window.Close();
                    }
                    // Disabling access must still work if the edit box contains an invalid port.
                    connection.PortText = "bad";
                    connection.ToggleServerCommand.Execute(null);
                    await WaitUntilAsync(() => !server.IsRunning && connection.CanEdit, token);
                    Check(!connection.CopyPromptCommand.CanExecute(null) && saved?.Enabled == false, "Disable ignores an unfinished port edit and persists the preference");
                    var disabledPrompt = connection.BuildPrompt();
                    connection.ApplyPortCommand.Execute(null);
                    await WaitUntilAsync(() => connection.CanEdit, token);
                    Check(connection.Feedback.Contains("1024") && connection.BuildPrompt() == disabledPrompt, "Invalid port is rejected without changing connection settings");
                    connection.PortText = FreePort().ToString();
                    connection.ToggleServerCommand.Execute(null);
                    await WaitUntilAsync(() => server.IsRunning && connection.CanEdit, token);
                    Check(connection.CopyPromptCommand.CanExecute(null) && connection.Endpoint.EndsWith($":{connection.PortText}/mcp"), "Changing port restarts server and updates the generated connection");
                    await connection.DisposeAsync();
                    Check(!server.IsRunning && !connection.CopyPromptCommand.CanExecute(null), "Closing application connection releases server and disables commands");
                    app.Shutdown();
                    completion.SetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "MCP WPF integration checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(20, token);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string name, Dictionary<string, object?> arguments, CancellationToken token)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: token);
        if (result.IsError == true) throw new InvalidOperationException(name + ": " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        using var document = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return document.RootElement.Clone();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        _checks++;
        Console.WriteLine("PASS: " + label);
    }

    private static async Task ThrowsAsync(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            Check(true, label);
            return;
        }
        throw new InvalidOperationException("Expected rejection: " + label);
    }
}
