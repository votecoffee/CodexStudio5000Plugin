using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class DiagnosticsForm : WorkspaceForm
{
    private sealed record ActionItem(string Name, string Title) { public override string ToString() => Title; }
    private sealed record Workflow(string Title, string Help, ActionItem[] Actions) { public override string ToString() => Title; }
    private sealed record WatchChoice(WatchStatus Status) { public override string ToString() => Status.Definition.Settings.Name + " • r" + Status.Definition.Revision + " • " + Status.State; }
    private static ActionItem A(string name, string title) => new("studio_" + name, title);
    private static readonly Workflow[] Workflows = [
        new("Logic tracing", "Select the modified/project snapshot, a tag and optional program. Trace readers/writers and dependencies across routines, aliases and AOI parameters. Review diagnostics for unknown effects, addressing and protected source.",
            [A("logic_trace", "Trace readers, writers and dependencies")]),
        new("Rename controller tag", "Select the source project and controller tag. Preview an exact new name in the operation editor. Review scoped references, program-local shadowing and capture conflicts. Unknown operands require exact reasoned decisions. Copy reviewSha256 into Apply to publish a separate project and receipt; source/data stay unchanged. Safety changes block by default and require explicit review-only handling. Native compile and external/HMI references require separate review.",
            [A("controller_tag_rename_preview", "Preview controller-tag rename and references"), A("controller_tag_rename_apply", "Apply reviewed rename to new project")]),
        new("Module connection timing", "Select the source project. Browse exact module/connection names, existing timing values and safety metadata; fill those identities and before-values in Preview. Supports RPI, NetworkDelayMultiplier and ReactionTimeLimit only. Safety edits block by default; explicitly choose review-only when authorized. Copy reviewSha256 into Apply to publish a separate review copy and receipt. Related values are not recalculated; native import/readback and safety acceptance remain separate.",
            [A("module_connections", "Browse connection timing and safety metadata"), A("module_connections_preview", "Preview exact connection attribute edits"), A("module_connections_apply", "Apply reviewed timing to new review copy")]),
        new("Captured rung explanation", "Freeze saved values or an existing sample, then evaluate a rung using the returned capture ID/hash and the exact routine XPath. Conditions are true, false or unknown. This explains captured predicates; it does not simulate controller execution.",
            [A("diagnostic_capture", "Capture selected values"), A("diagnostic_captures", "List captures"), A("rung_evaluate", "Explain captured rung")]),
        new("Triggered recording", "Save a trigger against the selected project/watch revision, then arm it. Start the authorized watch separately in Watch lists. Inspect episodes after change, predicate, timeout or communication fault; capture a sampled frame for rung explanation. Service restart leaves triggers disarmed.",
            [A("diagnostic_trigger_save", "Create or modify trigger"), A("diagnostic_triggers", "Read trigger states and episode IDs"), A("diagnostic_trigger_arm", "Arm or disarm trigger"), A("diagnostic_episode", "Read captured episode"), A("diagnostic_episode_capture", "Capture episode frame"), A("diagnostic_trigger_delete", "Remove disarmed trigger definition")]),
        new("Merge logic, keep current values", "Choose a common baseline, reviewed logic and fresh saved values. Mode preserve-fresh retains every fresh tag representation and configuration byte outside reviewed program routines and exact MESSAGE field choices. Review field conflicts and copy the review hash into Apply. Select the merged candidate for validation and a retained receipt. Native import is separate.",
            [A("logic_value_merge_preview", "Preview three-source merge and conflicts"), A("logic_value_merge_apply", "Apply exact reviewed merge to new project")]),
        new("Preservation and integrity validation", "Select the candidate separately from reviewed logic. Merge-preservation requires the same three-source policy and policySha256 from its preview. Review new/inherited/resolved findings and unsupported coverage; record its exact reviewSha256. Native-readback compares an existing export with the selected intended project without SDK jobs. Normalizations are explicit options.",
            [A("validation_profiles", "Discover named checks and coverage"), A("validation_preview", "Preview merge-preservation checks"), A("validation_preview", "Preview existing native readback checks"), A("validation_record", "Retain reviewed validation receipt"), A("validation_receipt", "Read hash-bound validation receipt")]),
        new("Version compatibility", "Select a saved source and target major revision. Preview first, then supply its review hash to Start. Native conversion uses a private copy. After successful conversion, request separate ACD readback and inspect its status/configuration differences. Downgrades are unsupported; native installation and licensing are required.",
            [A("version_compatibility_preview", "Preview target-version copy"), A("version_compatibility_start", "Start reviewed native conversion"), A("version_compatibility_readback", "Reopen converted ACD for readback"), A("version_compatibility_status", "Read jobs, differences and native messages")]),
        new("Legacy review copy", "REVIEW ONLY v35-to-v32 legacy input, separate from upgrade-only version conversion. Supply a completed offline SDK native L5K export job/artifact and its hash. Preview exact fields, then prepare with its review hash. Native import/save/readback are explicit separate SDK jobs. Compare actual v32 readback against the intended v35 source; review motion changes and protected AOI envelope/payload findings. Preparation does not establish compatibility or authorize deployment.",
            [A("legacy_review_preview", "Preview exact legacy review-input edits"), A("legacy_review_prepare", "Prepare immutable review input and manifest"), A("validation_preview", "Preview legacy review readback checks")]),
        new("Saved trends and results", "Use Trends in Watch lists for a graph, saved series/window and statistics. These editors also create/modify views, gather bounded result pages and export an exact result hash. HasMore means statistics are partial. Creating views or gathering results never starts polling.",
            [A("trend_views", "List saved trend views"), A("trend_view_get", "Read saved view"), A("trend_view_save", "Create or modify saved trend view"), A("trend_analyze", "Gather trend values, statistics and gaps"), A("trend_export", "Export exact analyzed results"), A("trend_view_delete", "Remove saved view")])
    ];
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly WatchStatus? initialWatch;
    private readonly ComboBox project = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox baseline = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox fresh = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox candidate = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox watch = new() { Width = 370, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox tag = new() { Width = 200, PlaceholderText = "Tag name" };
    private readonly TextBox program = new() { Width = 200, PlaceholderText = "Program (empty = controller)" };
    private readonly NumericUpDown target = new() { Width = 65, Minimum = 31, Maximum = 38, Value = 38 };
    private readonly ListBox workflows = new() { Dock = DockStyle.Fill };
    private readonly ListBox actions = new() { Dock = DockStyle.Fill };
    private readonly Label help = new() { Dock = DockStyle.Top, Height = 160, Padding = new(12) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 55, Padding = new(10) };
    private DiagnosticCaptureInfo[] captures = [];
    private DiagnosticTriggerStatus[] triggers = [];
    private SavedTrendSummary[] trends = [];
    private bool fixture;
    public DiagnosticsForm(ServiceClient client, ProjectInfo? initial = null, WatchStatus? initialWatch = null)
    {
        this.client = client; this.initial = initial; this.initialWatch = initialWatch;
        Text = "Diagnostics, trends and project workflows"; Size = new(1240, 800); MinimumSize = new(1100, 720); Font = new("Segoe UI", 10);
        var header = new Label { Text = "DIAGNOSTICS & PROJECT WORKFLOWS", UseMnemonic = false, Dock = DockStyle.Top, Height = 52, Padding = new(14), BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White };
        var context = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 185, Padding = new(8) };
        void Field(string label, Control value) { context.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new(2, 7, 0, 0) }); context.Controls.Add(value); }
        Field("Project / modified logic", project); Field("Tag", tag); Field("Program", program); context.SetFlowBreak(program, true);
        Field("Common baseline", baseline); Field("Fresh saved values", fresh); context.SetFlowBreak(fresh, true);
        Field("Candidate / native readback", candidate); context.SetFlowBreak(candidate, true);
        Field("Watch", watch); Field("Target major", target);
        var refresh = new Button { Text = "Refresh inventory", AutoSize = true }; refresh.Click += async (_, _) => await Run(RefreshAsync); context.Controls.Add(refresh);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 }; split.Panel1.Controls.Add(workflows);
        split.Panel2.Controls.Add(actions); split.Panel2.Controls.Add(help);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new(8) };
        var open = new Button { Text = "Open selected operation…", AutoSize = true }; bar.Controls.Add(open);
        bar.Controls.Add(new Label { Text = "Review the populated arguments before running. Results remain copyable in the operation window.", AutoSize = true, Padding = new(8, 7, 0, 0) });
        Controls.Add(split); Controls.Add(status); Controls.Add(bar); Controls.Add(context); Controls.Add(header);
        workflows.Items.AddRange(Workflows); workflows.SelectedIndexChanged += (_, _) => SelectWorkflow(); workflows.SelectedIndex = 0;
        open.Click += (_, _) => { if (actions.SelectedItem is ActionItem action) { using var editor = new ExtensionsForm(client, action.Name, Seed(action.Name)); editor.ShowDialog(this); } };
        Shown += async (_, _) => { split.SplitterDistance = 280; if (!fixture) await Run(RefreshAsync); };
    }
    private void SelectWorkflow()
    {
        if (workflows.SelectedItem is not Workflow w) return;
        help.Text = w.Help;
        actions.BeginUpdate();
        try { actions.Items.Clear(); actions.Items.AddRange(w.Actions); if (actions.Items.Count > 0) actions.SelectedIndex = 0; }
        finally { actions.EndUpdate(); }
    }
    private async Task Run(Func<Task> action)
    { Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { Enabled = true; } }
    private async Task RefreshAsync()
    {
        var projects = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").ToArray();
        var selected = (project.SelectedItem as ProjectInfo)?.Id ?? initial?.Id;
        foreach (var combo in new[] { project, baseline, fresh, candidate }) combo.DataSource = projects.ToArray();
        project.SelectedItem = projects.FirstOrDefault(p => p.Id == selected) ?? projects.FirstOrDefault();
        var watches = await client.GetAsync<WatchStatus[]>("/api/watches"); var selectedWatch = (watch.SelectedItem as WatchChoice)?.Status.Definition.Id ?? initialWatch?.Definition.Id;
        watch.DataSource = watches.Select(w => new WatchChoice(w)).ToArray();
        watch.SelectedItem = ((WatchChoice[])watch.DataSource).FirstOrDefault(w => w.Status.Definition.Id == selectedWatch) ?? ((WatchChoice[])watch.DataSource).FirstOrDefault();
        captures = await client.GetAsync<DiagnosticCaptureInfo[]>("/api/diagnostics/captures"); triggers = await client.GetAsync<DiagnosticTriggerStatus[]>("/api/diagnostics/triggers"); trends = await client.GetAsync<SavedTrendSummary[]>("/api/trends");
        status.Text = $"{projects.Length} saved Logix snapshots • {watches.Length} watch lists • {captures.Length} captures • {triggers.Length} triggers • {trends.Length} saved trends.\nInventory refresh does not open a project natively or connect to a controller.";
    }
    private JsonObject Seed(string operation)
    {
        var p = project.SelectedItem as ProjectInfo; var w = (watch.SelectedItem as WatchChoice)?.Status;
        var b = baseline.SelectedItem as ProjectInfo; var f = fresh.SelectedItem as ProjectInfo;
        var selectedTag = new WatchTag("selected", tag.Text, string.IsNullOrWhiteSpace(program.Text) ? null : program.Text);
        var capture = captures.FirstOrDefault(c => c.ProjectId == p?.Id); var trigger = triggers.FirstOrDefault(t => t.Definition.Watch.Id == w?.Definition.Id); var view = trends.FirstOrDefault(t => t.WatchId == w?.Definition.Id);
        var merge = new LogicValueMergeRequest(new(b?.Id ?? "", b?.CurrentSha256 ?? ""), new(p?.Id ?? "", p?.CurrentSha256 ?? ""), new(f?.Id ?? "", f?.CurrentSha256 ?? ""), Mode: "preserve-fresh");
        var selectedCandidate = candidate.SelectedItem as ProjectInfo;
        var validation = new LogixValidationRequest("merge-preservation", new(selectedCandidate?.Id ?? "", selectedCandidate?.CurrentSha256 ?? ""), Merge: merge, ExpectedPolicySha256: "");
        var version = new VersionCompatibilityRequest(p?.Id ?? "", p?.CurrentSha256 ?? "", (int)target.Value);
        var legacy = new LegacyReviewRequest("", "", "", "Review-only legacy copy");
        var rename = new ControllerTagRenameRequest(p?.Id ?? "", p?.CurrentSha256 ?? "", tag.Text, "");
        var connections = new ModuleConnectionEditRequest(p?.Id ?? "", p?.CurrentSha256 ?? "", [new("", "", "RPI", "", "")]);
        object? request = operation switch
        {
            "studio_logic_trace" => new LogicTraceRequest(p?.CurrentSha256 ?? "", tag.Text, selectedTag.ProgramName),
            "studio_controller_tag_rename_preview" => rename,
            "studio_controller_tag_rename_apply" => new ControllerTagRenameApplyRequest(rename, ""),
            "studio_module_connections" => new ModuleConnectionBrowseRequest(p?.Id ?? "", p?.CurrentSha256 ?? ""),
            "studio_module_connections_preview" => connections,
            "studio_module_connections_apply" => new ModuleConnectionApplyRequest(connections, ""),
            "studio_diagnostic_capture" => new DiagnosticCaptureRequest(p?.Id ?? "", p?.CurrentSha256 ?? "", w?.Definition.Settings.Tags ?? [selectedTag]),
            "studio_rung_evaluate" => new RungEvaluationRequest(capture?.Id ?? "", capture?.Artifact.Sha256 ?? "", "/RSLogix5000Content/Controller/Programs/Program[@Name='" + program.Text + "']/Routines/Routine[@Name='main']", 0),
            "studio_diagnostic_trigger_save" => new DiagnosticTriggerSaveRequest("Diagnostic window", p?.Id ?? "", p?.CurrentSha256 ?? "", w?.Definition.Id ?? "", w?.Definition.Revision ?? 0, new("change", w?.Definition.Settings.Tags.FirstOrDefault()?.Key)),
            "studio_diagnostic_trigger_arm" => new DiagnosticTriggerArmRequest(trigger?.Definition.Revision ?? 0),
            "studio_diagnostic_trigger_delete" => new ReviewDeleteRequest(trigger?.Definition.Revision ?? 0),
            "studio_diagnostic_episode_capture" => new DiagnosticEpisodeCaptureRequest(trigger?.Episodes.FirstOrDefault()?.Artifact.Sha256 ?? ""),
            "studio_logic_value_merge_preview" => merge,
            "studio_logic_value_merge_apply" => new LogicValueMergeApplyRequest(merge, ""),
            "studio_validation_preview" => actions.SelectedItem is ActionItem { Title: "Preview existing native readback checks" or "Preview legacy review readback checks" } selected ? new LogixValidationRequest(selected.Title.Contains("legacy", StringComparison.Ordinal) ? "legacy-review-readback" : "native-readback", validation.Candidate, Reference: new(p?.Id ?? "", p?.CurrentSha256 ?? ""), NativeRules: new()) : validation,
            "studio_legacy_review_preview" => legacy,
            "studio_legacy_review_prepare" => new LegacyReviewPrepareRequest(legacy, ""),
            "studio_validation_record" => new LogixValidationRecordRequest(validation, ""),
            "studio_version_compatibility_preview" => version,
            "studio_version_compatibility_start" => new VersionCompatibilityStartRequest(version, ""),
            "studio_trend_view_save" => new SavedTrendRequest("Saved trend", w?.Definition.Id ?? "", w?.Definition.Revision ?? 0, w?.Definition.Settings.Tags.Select(t => t.Key).ToArray() ?? ["selected"]),
            "studio_trend_view_delete" => new ReviewDeleteRequest(view?.Revision ?? 0),
            "studio_trend_analyze" => new TrendAnalyzeRequest(view?.Id ?? "", view?.Revision ?? 0),
            "studio_trend_export" => new TrendExportRequest(new(view?.Id ?? "", view?.Revision ?? 0), ""), _ => null
        };
        var seed = new JsonObject(); if (request is not null) seed["request"] = JsonSerializer.SerializeToNode(request, ProjectService.Json);
        if (operation == "studio_logic_trace") seed["projectId"] = p?.Id ?? "";
        if (operation.Contains("episode", StringComparison.Ordinal) || operation is "studio_diagnostic_trigger_arm" or "studio_diagnostic_trigger_delete") seed["triggerId"] = trigger?.Definition.Id ?? "";
        if (operation.Contains("episode", StringComparison.Ordinal)) seed["episodeId"] = trigger?.Episodes.FirstOrDefault()?.Id ?? "";
        if (operation is "studio_trend_view_get" or "studio_trend_view_delete") seed["trendId"] = view?.Id ?? "";
        if (operation is "studio_version_compatibility_status" or "studio_version_compatibility_readback") seed["runId"] = "";
        if (operation == "studio_validation_receipt") { seed["receiptId"] = ""; seed["expectedSha256"] = ""; }
        return seed;
    }
    internal async Task<string[]> ExerciseOfflineWorkflowForTestAsync()
    {
        fixture = true; await RefreshAsync(); tag.Text = "Record"; program.Text = "WatchProgram";
        var checkedOperations = new List<string>();
        foreach (var workflow in Workflows)
        {
            workflows.SelectedItem = workflow;
            foreach (var action in workflow.Actions)
            { actions.SelectedItem = action; using var editor = new ExtensionsForm(client, action.Name, Seed(action.Name)); if (await editor.ValidatePreparedForTestAsync() != action.Name) throw new InvalidOperationException("Incorrect workflow operation selected."); checkedOperations.Add(action.Name); }
        }
        workflows.SelectedIndex = 0; return checkedOperations.ToArray();
    }
}
