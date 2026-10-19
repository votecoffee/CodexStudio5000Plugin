using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Desktop;

ApplicationConfiguration.Initialize();
try
{
    var root = ServiceClient.FindRoot();
    using var client = new ServiceClient(root);
    if (args.Length == 2 && args[0] == "--self-test-settings-workflow")
    {
        SettingsForm? settingsView = null; string[] checks = [];
        WorkflowValidation.Run(async () => { settingsView = new SettingsForm(client); checks = await settingsView.ExerciseOfflineWorkflowForTestAsync(); });
        using var view = settingsView ?? throw new InvalidOperationException("Missing settings view.");
        view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false; view.Show(); Application.DoEvents();
        File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); view.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-transactions-workflow")
    {
        TransactionForm? transactionView = null; string[] checks = [];
        WorkflowValidation.Run(async () =>
        {
            var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "MultilingualDemo.L5X")));
            transactionView = new TransactionForm(client, project); checks = await transactionView.ExerciseOfflineWorkflowForTestAsync(root);
        });
        using var view = transactionView ?? throw new InvalidOperationException("Missing transaction view."); view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false; view.Show(); Application.DoEvents();
        File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); view.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-hmi-languages-workflow" or "--self-test-viewdesigner-languages-workflow")
    {
        HmiLanguageForm? languageView = null; string[] checks = [];
        WorkflowValidation.Run(async () =>
        {
            var viewDesigner = args[0] == "--self-test-viewdesigner-languages-workflow";
            var workspace = await client.PostAsync<HmiLanguageInfo>("/api/hmi-languages", new HmiLanguageRegisterRequest(Path.Combine(root, "examples", viewDesigner ? "ViewDesignerV9LanguagesDemo.xlsx" : "FactoryTalkLanguagesDemo.xls"), viewDesigner ? "view-designer" : "ftview-me"));
            languageView = new HmiLanguageForm(client, initial: workspace); checks = await languageView.ExerciseOfflineWorkflowForTestAsync(root);
        });
        using var view = languageView ?? throw new InvalidOperationException("Missing HMI language view."); view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false; view.Show(); Application.DoEvents();
        File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); view.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-documentation-workflow")
    {
        DocumentationForm? documentationView = null; string[] checks = [];
        WorkflowValidation.Run(async () =>
        {
            var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "MultilingualDemo.L5X")));
            documentationView = new DocumentationForm(client, project); checks = await documentationView.ExerciseOfflineWorkflowForTestAsync(root);
        });
        using var view = documentationView ?? throw new InvalidOperationException("Missing documentation view."); view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false; view.Show(); Application.DoEvents();
        File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); view.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-diagnostics-workflow" or "--self-test-trend-views-workflow")
    {
        Form? view = null; string[] checks = [];
        WorkflowValidation.Run(async () =>
        {
            var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "WatchDemo.L5X")));
            var watch = await client.PostAsync<WatchStatus>("/api/tags/watch", new BrowserWatchRequest(project.Id, project.CurrentSha256, "Generated desktop diagnostic watch", [new("count", "Record", "WatchProgram", "Inner.Count")]));
            if (args[0] == "--self-test-diagnostics-workflow")
            { var diagnostics = new DiagnosticsForm(client, project, watch); view = diagnostics; checks = await diagnostics.ExerciseOfflineWorkflowForTestAsync(); }
            else { var trends = new TrendForm(client, watch); view = trends; checks = await trends.ExerciseSavedViewsForTestAsync(root); }
        });
        using (var renderedView = view ?? throw new InvalidOperationException("Missing generated workflow view."))
        {
            renderedView.StartPosition = FormStartPosition.Manual; renderedView.Location = new(-30000, -30000); renderedView.ShowInTaskbar = false; renderedView.Show(); Application.DoEvents();
            File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
            using var bitmap = new Bitmap(renderedView.Width, renderedView.Height); renderedView.DrawToBitmap(bitmap, new Rectangle(Point.Empty, renderedView.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); renderedView.Close();
        }
        return;
    }
    if (args.Length == 2 && args[0] is "--self-test-comparison-annotations" or "--self-test-comparison-annotations-workflow")
    {
        var workflow = args[0].EndsWith("-workflow", StringComparison.Ordinal);
        var a = new ComparisonSnapshot(new string('a', 32), "Baseline example", 0, new string('a', 64)); var b = new ComparisonSnapshot(new string('b', 32), "Edited example", 1, new string('b', 64));
        var entry = new ComparisonReviewEntry(new string('c', 32), DateTimeOffset.UnixEpoch, "html", new string('c', 32), new string('d', 64), 1024, a, b, null, false, false, true, 3, 3);
        using var annotations = new ComparisonAnnotationsForm(client, entry); annotations.LoadForTest(workflow);
        annotations.StartPosition = FormStartPosition.Manual; annotations.Location = new(-30000, -30000); annotations.ShowInTaskbar = false;
        annotations.Show(); Application.DoEvents();
        if (workflow) File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(
            new { checks = annotations.ExerciseOfflineWorkflowForTest(root), controllerConnectionAttempted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(annotations.Width, annotations.Height); annotations.DrawToBitmap(bitmap, new Rectangle(Point.Empty, annotations.Size));
        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); annotations.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-comparison-history" or "--self-test-comparison-history-workflow")
    {
        var workflow = args[0].EndsWith("-workflow", StringComparison.Ordinal);
        using var history = new ComparisonReviewHistoryForm(client); history.LoadForTest(workflow);
        history.StartPosition = FormStartPosition.Manual; history.Location = new(-30000, -30000); history.ShowInTaskbar = false;
        history.Show(); Application.DoEvents();
        if (workflow) File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(
            new { checks = history.ExerciseOfflineWorkflowForTest(root), controllerConnectionAttempted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(history.Width, history.Height); history.DrawToBitmap(bitmap, new Rectangle(Point.Empty, history.Size));
        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); history.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-merge-mappings" or "--self-test-merge-mappings-workflow")
    {
        using var mappings = new MergeMappingForm(client, []); mappings.LoadForTest();
        mappings.StartPosition = FormStartPosition.Manual; mappings.Location = new(-30000, -30000); mappings.ShowInTaskbar = false;
        mappings.Show(); Application.DoEvents();
        if (args[0].EndsWith("-workflow", StringComparison.Ordinal)) File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(
            new { checks = mappings.ExerciseOfflineWorkflowForTest(), controllerConnectionAttempted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(mappings.Width, mappings.Height); mappings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, mappings.Size));
        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); mappings.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-comparison-workflow")
    {
        using var comparison = new ProjectCompareForm(client, null); comparison.LoadForTest(workflow: true);
        comparison.StartPosition = FormStartPosition.Manual; comparison.Location = new(-30000, -30000); comparison.ShowInTaskbar = false;
        comparison.Show(); Application.DoEvents();
        File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks = comparison.ExerciseOfflineWorkflowForTest(root), controllerConnectionAttempted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(comparison.Width, comparison.Height); comparison.DrawToBitmap(bitmap, new Rectangle(Point.Empty, comparison.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); comparison.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-value-merge" or "--self-test-value-merge-workflow")
    {
        var workflow = args[0] == "--self-test-value-merge-workflow";
        using var merge = new TagValueMergeForm(client, null); merge.LoadForTest(workflow);
        merge.StartPosition = FormStartPosition.Manual; merge.Location = new(-30000, -30000); merge.ShowInTaskbar = false;
        merge.Show(); Application.DoEvents();
        if (workflow) File.WriteAllText(args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { checks = merge.ExerciseOfflineWorkflowForTest(root), controllerConnectionAttempted = false }, ProjectService.Json));
        using var bitmap = new Bitmap(merge.Width, merge.Height);
        merge.DrawToBitmap(bitmap, new Rectangle(Point.Empty, merge.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); merge.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-watch-rebind")
    {
        var tags = new WatchTag[] { new("count", "Record", "WatchProgram", "Inner.Count") };
        var definition = new WatchDefinition(new string('0', 32), 2, DateTimeOffset.UtcNow, new("Process watch", tags, new string('a', 64), SourcePath: "Fixture.L5X", Mode: "Offline"));
        var stopped = new WatchStatus(definition, "stopped", null, true, null, []);
        var proposed = new BrowserWatchRequest("fixture", new string('b', 64), "Process watch", tags);
        var preflight = new WatchPreflightReport("Sdk", "Offline", null, null, proposed.ExpectedSha256, true, "blocked",
            [new("reader_configuration", "blocked", new("native_unavailable", "Reader is not configured. A checked stopped rebind remains available."))],
            [new(tags[0], "supported", "LINT", [], null, [])], null, "Generated saved declarations only; no controller connection.");
        var report = new WatchRebindPreviewReport(definition.Id, 2, definition.Settings.ExpectedSha256, definition.Settings.ExpectedSha256, "matching_l5x", new string('c', 64), proposed, true, [],
            [new(tags[0], tags[0], new("DINT", [], "WatchRecord", new string('d', 64), true), new("LINT", [], "WatchRecord", new string('e', 64), true), ["selected_type_changed", "base_declaration_changed"], [])],
            preflight, "Rebind retains this list ID, settings and retained recording history. It creates revision 3, clears cached values and leaves polling stopped. Declaration checks do not establish native/live behavior.");
        using var rebindView = new WatchRebindForm(client, stopped, null); rebindView.LoadForTest(report);
        rebindView.StartPosition = FormStartPosition.Manual; rebindView.Location = new(-30000, -30000); rebindView.ShowInTaskbar = false;
        rebindView.Show(); Application.DoEvents(); using var bitmap = new Bitmap(rebindView.Width, rebindView.Height);
        rebindView.DrawToBitmap(bitmap, new Rectangle(Point.Empty, rebindView.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); rebindView.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-watch-template")
    {
        var portable = new WatchTemplate(1, "Reusable process watch", [new("count", "Record", "WatchProgram", "Inner.Count")], 1000, 100);
        var proposed = new BrowserWatchRequest("fixture", new string('a', 64), portable.Name, portable.Tags);
        var preflight = new WatchPreflightReport("Sdk", "Offline", null, null, proposed.ExpectedSha256, true, "blocked",
            [new("source_hash", "passed"), new("reader_configuration", "blocked", new("native_unavailable", "SDK reader is not configured; this does not prevent saving a stopped list."))],
            [new(portable.Tags[0], "supported", "DINT", [], null, [])], null, "Generated declarations only; no connection.");
        var preview = new WatchTemplatePreviewReport(new string('b', 64), new string('c', 64), proposed, true, [], preflight,
            "Import creates a NEW stopped list. Source paths, target routes, mode, live values and recordings are not carried by the template.");
        using var templateForm = new WatchTemplateForm(client, null); templateForm.LoadForTest(System.Text.Json.JsonSerializer.SerializeToElement(portable, ProjectService.Json), preview);
        templateForm.StartPosition = FormStartPosition.Manual; templateForm.Location = new(-30000, -30000); templateForm.ShowInTaskbar = false;
        templateForm.Show(); Application.DoEvents(); using var bitmap = new Bitmap(templateForm.Width, templateForm.Height);
        templateForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, templateForm.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); templateForm.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-watch-preflight")
    {
        var fixture = new Studio5000.Core.WatchPreflightReport("LibPlcTag", "Online", null, null, new string('a', 64), true, "blocked",
            [new("source_hash", "passed"), new("controller_access", "blocked", new("sdk_controller_disabled", "Controller access is disabled.")),
             new("reader_configuration", "blocked", new("cip_unavailable", "The optional reader is not configured.")),
             new("read_budget", "deferred", new("cip_partial_budget", "Counts cover supported rows only."))],
            [new(new("record", "Record", "WatchProgram"), "supported", "WatchRecord", [], 6, []),
             new(new("count", "Record", "WatchProgram", "Inner.Count"), "supported", "DINT", [], 1, []),
             new(new("message", "Message", "WatchProgram"), "blocked", null, [], null, [new("cip_type_unsupported", "STRING requires the SDK backend.")])],
            new(2, 6, 9, 18, 6, false, true), "Generated local inspection fixture; no controller connection or native verification.");
        using var preflight = new WatchPreflightForm(fixture, "Generated process watch");
        preflight.StartPosition = FormStartPosition.Manual; preflight.Location = new(-30000, -30000); preflight.ShowInTaskbar = false;
        preflight.Show(); Application.DoEvents(); using var bitmap = new Bitmap(preflight.Width, preflight.Height);
        preflight.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preflight.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        preflight.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-comparison" or "--self-test-comparison-ladder" or "--self-test-ladder" or "--self-test-references" or "--self-test-reference-parameters" or "--self-test-tag-references")
    {
        using Form review = args[0] switch { "--self-test-comparison" or "--self-test-comparison-ladder" => new ProjectCompareForm(client, null), "--self-test-references" or "--self-test-reference-parameters" => new GraphicsReferencesForm(client, null), "--self-test-tag-references" => new TagReferencesForm(client, null), _ => new LadderForm(client, null) };
        review.StartPosition = FormStartPosition.Manual; review.Location = new(-30000, -30000); review.ShowInTaskbar = false;
        if (review is ProjectCompareForm comparison) comparison.LoadForTest(args[0] == "--self-test-comparison-ladder"); else if (review is GraphicsReferencesForm resolver) resolver.LoadForTest(args[0] == "--self-test-reference-parameters"); else if (review is TagReferencesForm whereUsed) whereUsed.LoadForTest(); else ((LadderForm)review).LoadForTest();
        review.Show(); Application.DoEvents(); using var bitmap = new Bitmap(review.Width, review.Height);
        review.DrawToBitmap(bitmap, new Rectangle(Point.Empty, review.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        review.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-browser" or "--self-test-trends")
    {
        using Form view = args[0] == "--self-test-browser" ? new TagBrowserForm(client, null) : new TrendForm(client,
            new(new("00000000000000000000000000000000", 0, DateTimeOffset.UtcNow, new("Process values", [new("Count", "Record", "WatchProgram", "Inner.Count")], new string('0', 64), SourcePath: "Fixture.L5X", Mode: "Offline")), "stopped", null, true, null, []));
        view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false;
        if (view is TagBrowserForm browser)
        {
            browser.LoadForTest(new("fixture", new string('a', 64), "WatchDemo", ["WatchProgram"],
                [new("WatchRecord", null, [new("Inner", "WatchInner", [], false, null, null, null), new("Samples", "DINT", [3], false, null, null, null)])],
                [new("Record", "tag", "WatchRecord", [], "WatchProgram", "Record", null, true, true), new("Records", "tag", "WatchRecord", [2], "WatchProgram", "Records", null, true, true),
                 new("Enabled", "tag", "BOOL", [], "WatchProgram", "Enabled", null, false, true)], 3, 0, 100));
        }
        else if (view is TrendForm trends)
        {
            var start = DateTimeOffset.UtcNow.AddSeconds(-60); var records = new List<Studio5000.Core.WatchRecord>();
            records.Add(new(1, 0, "one", start, "start"));
            for (var i = 0; i < 55; i++)
            {
                var at = start.AddSeconds(i + 1);
                if (i == 22) records.Add(new(records.Count + 1, 0, "one", at, "fault", Detail: "Communication interrupted"));
                if (i is >= 22 and < 32) continue;
                var sample = new Studio5000.Core.WatchSnapshot(i + 1, at.AddMilliseconds(-30), at, "Offline",
                    [new("Count", "Record", "WatchProgram", "DINT", System.Text.Json.Nodes.JsonValue.Create(8 + Math.Sin(i * .3) * 2), "good"),
                     new("Enabled", "Enabled", "WatchProgram", "BOOL", System.Text.Json.Nodes.JsonValue.Create(i % 15 < 8), "good")]);
                records.Add(new(records.Count + 1, 0, i < 32 ? "one" : "two", at, "sample", sample));
            }
            records.Add(new(records.Count + 1, 0, "two", start.AddSeconds(57), "stop")); trends.LoadForTest(records.ToArray());
        }
        view.Show(); Application.DoEvents(); using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); view.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-extensions")
    {
        using var extensions = new ExtensionsForm(client);
        extensions.StartPosition = FormStartPosition.Manual; extensions.Location = new(-30000, -30000); extensions.ShowInTaskbar = false;
        extensions.LoadForTest(); extensions.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(extensions.Width, extensions.Height);
        extensions.DrawToBitmap(bitmap, new Rectangle(Point.Empty, extensions.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        extensions.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-viewdesigner")
    {
        using var view = new ViewDesignerForm(client);
        view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false;
        view.LoadForTest(); view.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        view.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-watches" or "--self-test-watches-values")
    {
        using var watches = new WatchForm(client, null);
        watches.StartPosition = FormStartPosition.Manual; watches.Location = new(-30000, -30000); watches.ShowInTaskbar = false;
        var settings = new Studio5000.Core.WatchListRequest("UDT process values", [new("record", "Record", "WatchProgram")], new string('0', 64), SourcePath: "Example.ACD", Mode: "Offline");
        var sample = new Studio5000.Core.WatchSnapshot(3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Offline",
            [new("record", "Record", "WatchProgram", "WatchRecord", System.Text.Json.Nodes.JsonNode.Parse("{\"Count\":7,\"Position\":1.5,\"Inner\":{\"Count\":8},\"Samples\":[10,20,30]}"), "good")]);
        watches.LoadForTest(new(new("00000000000000000000000000000000", 0, DateTimeOffset.UtcNow, settings), "stopped", sample, true, null, []), showValues: args[0].EndsWith("values", StringComparison.Ordinal));
        watches.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(watches.Width, watches.Height);
        watches.DrawToBitmap(bitmap, new Rectangle(Point.Empty, watches.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        watches.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-online-edit")
    {
        using var online = new OnlineEditDialog(client);
        online.StartPosition = FormStartPosition.Manual; online.Location = new(-30000, -30000); online.ShowInTaskbar = false;
        online.LoadForTest(); online.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(online.Width, online.Height);
        online.DrawToBitmap(bitmap, new Rectangle(Point.Empty, online.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        online.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-sdk")
    {
        using var sdk = new SdkForm(client, null);
        sdk.StartPosition = FormStartPosition.Manual; sdk.Location = new(-30000, -30000); sdk.ShowInTaskbar = false;
        sdk.LoadForTest();
        sdk.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(sdk.Width, sdk.Height);
        sdk.DrawToBitmap(bitmap, new Rectangle(Point.Empty, sdk.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        sdk.Close(); return;
    }
    using var form = new MainForm(client);
    if (args.Length == 2 && args[0] == "--self-test")
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show(); // create the real control handles off screen
        Application.DoEvents();
        form.LoadForTest(Path.Combine(root, "examples", Environment.GetEnvironmentVariable("STUDIO5000_GUI_FIXTURE") ?? "Demo.L5X"));
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        return;
    }
    form.Shown += async (_, _) => await form.InitializeAsync(args.Length == 2 && args[0] == "--project" ? args[1] : null);
    Application.Run(form);
}
catch (Exception ex)
{
    if (args.Any(a => a.StartsWith("--self-test", StringComparison.Ordinal))) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    else MessageBox.Show(ex.Message, Studio5000.Core.WorkspaceIdentity.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
