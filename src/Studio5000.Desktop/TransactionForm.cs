using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class TransactionForm : WorkspaceForm
{
    private sealed record Choice(string Id, string Type, string Name, string Sha256) { public override string ToString() => Name + " [" + Type + "]"; }
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox workspaces = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox transactions = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox editor = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly TextBox review = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label metadata = new() { Dock = DockStyle.Top, Height = 72, Padding = new(8) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 56, Padding = new(8) };
    private readonly Button commit = new() { Text = "Commit reviewed transaction", AutoSize = true, Enabled = false };
    private WorkspaceTransactionInfo? current;
    private WorkspaceTransactionPreview? preview;
    private bool loading, busy, dirty, fixture;
    public TransactionForm(ServiceClient client, ProjectInfo? initial = null)
    {
        this.client = client; this.initial = initial; Text = "Concurrent workspaces and offline transactions"; Size = new(1420, 920); MinimumSize = new(1120, 750); Font = new("Segoe UI", 10);
        var header = new Label { Text = "OFFLINE TRANSACTIONS  /  STAGE • REVIEW • COMMIT", Dock = DockStyle.Top, Height = 48, Padding = new(12), BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new(8) };
        toolbar.Controls.Add(workspaces); Button(toolbar, "Begin private transaction", BeginAsync); toolbar.Controls.Add(transactions); Button(toolbar, "Refresh", RefreshAsync); Button(toolbar, "Scheduling status", SchedulingAsync);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(7) };
        Button(actions, "Stage editor JSON", StageAsync); Button(actions, "Review staged changes", PreviewAsync); actions.Controls.Add(commit); Button(actions, "Abort private transaction", AbortAsync); Button(actions, "Reset editor draft", () => { ResetEditor(); return Task.CompletedTask; });
        var left = new Panel { Dock = DockStyle.Fill }; left.Controls.Add(editor); left.Controls.Add(new Label { Text = "Next stage JSON — use exact selectors/keys from the project or language browser.", Dock = DockStyle.Top, Height = 40, Padding = new(7) });
        var split = new SplitContainer { Dock = DockStyle.Fill }; split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(review);
        var boundary = new Label { Text = "Stages are private and survive restart. One workspace per transaction; current files change only at commit. Native Studio/controller operations are excluded.", Dock = DockStyle.Bottom, Height = 42, Padding = new(7), BackColor = Color.FromArgb(255, 245, 220) };
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(boundary); Controls.Add(metadata); Controls.Add(toolbar); Controls.Add(header);
        editor.TextChanged += (_, _) => { if (!loading) { dirty = true; preview = null; commit.Enabled = false; } };
        transactions.SelectedIndexChanged += async (_, _) => { if (!loading) await Run(SelectAsync); };
        commit.Click += async (_, _) => await Run(CommitAsync);
        Shown += async (_, _) => { split.SplitterDistance = split.Width / 2; if (!fixture) await Run(RefreshAsync); };
        FormClosing += (_, e) => { if (dirty && !fixture) { e.Cancel = true; status.Text = "Stage the editor draft or Reset editor draft before closing. Private staged changes remain saved."; } };
    }
    private void Button(Control parent, string text, Func<Task> action) { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private async Task Run(Func<Task> action) { if (busy) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private async Task RefreshAsync()
    {
        var choices = new List<Choice>();
        choices.AddRange((await client.GetAsync<ProjectInfo[]>("/api/projects")).Select(p => new Choice(p.Id, "project", p.Name, p.CurrentSha256)));
        choices.AddRange((await client.GetAsync<ProjectInfo[]>("/api/viewdesigner/projects")).Select(p => new Choice(p.Id, "view-designer", p.Name, p.CurrentSha256)));
        choices.AddRange((await client.GetAsync<HmiLanguageInfo[]>("/api/hmi-languages")).Select(p => new Choice(p.Id, "hmi-languages", p.Name, p.CurrentSha256)));
        loading = true; workspaces.DataSource = choices; workspaces.SelectedItem = choices.FirstOrDefault(c => c.Id == current?.WorkspaceId) ?? choices.FirstOrDefault(c => c.Id == initial?.Id) ?? choices.FirstOrDefault(); loading = false;
        var page = await client.GetAsync<WorkspaceTransactionPage>("/api/transactions?limit=100"); loading = true; transactions.DataSource = page.Transactions; transactions.SelectedItem = page.Transactions.FirstOrDefault(t => t.Id == current?.Id); loading = false;
        if (current is not null) current = await client.GetAsync<WorkspaceTransactionInfo>("/api/transactions/" + current.Id);
        ShowState(); await SchedulingAsync();
    }
    private async Task SelectAsync()
    {
        if (dirty) { loading = true; transactions.SelectedItem = ((WorkspaceTransactionInfo[])transactions.DataSource!).FirstOrDefault(t => t.Id == current?.Id); loading = false; throw new InvalidOperationException("Stage or reset the editor draft before switching transactions."); }
        current = transactions.SelectedItem as WorkspaceTransactionInfo; preview = null; commit.Enabled = false;
        if (current is not null) current = await client.GetAsync<WorkspaceTransactionInfo>("/api/transactions/" + current.Id); ResetEditor(); ShowState();
    }
    private void ShowState()
    {
        if (current is null) { metadata.Text = "Choose a workspace and begin a private transaction, or select a retained transaction."; return; }
        metadata.Text = $"{current.Name} • {current.Status} • stage version {current.Version} • {current.Steps.Sum(s => s.Edits)} edits\nBase r{current.BaseRevision}: {current.BaseSha256[..12]}  →  staged: {current.StagedSha256[..12]}\nTransaction {current.Id}";
        if (current.Status != "open") { preview = null; commit.Enabled = false; }
    }
    private void ResetEditor()
    {
        if (current is null) return;
        var template = current.WorkspaceType switch
        {
            "hmi-languages" => new WorkspaceTransactionStageRequest(current.Version, LanguageEdits: [new("<key from language browser>", "fr-FR", "<translated text>")]),
            "view-designer" => new WorkspaceTransactionStageRequest(current.Version, ViewDesignerEdits: [new("<file from export inspection>", "<property selector>", "\"<new text>\"")]),
            _ => new WorkspaceTransactionStageRequest(current.Version, XmlEdits: [new("set_text", "<exact scalar Description/Text/Comment XPath>", "<new text>")])
        };
        loading = true; editor.Text = JsonSerializer.Serialize(template, ProjectService.Json); loading = false; dirty = false; preview = null; commit.Enabled = false;
    }
    private async Task BeginAsync()
    {
        if (dirty) throw new InvalidOperationException("Stage or reset the current editor draft first.");
        if (workspaces.SelectedItem is not Choice choice) throw new InvalidOperationException("Select a saved workspace.");
        current = await client.PostAsync<WorkspaceTransactionInfo>("/api/transactions", new WorkspaceTransactionBeginRequest(choice.Id, choice.Type, choice.Sha256, choice.Name + " transaction"));
        ResetEditor(); ShowState(); review.Text = JsonSerializer.Serialize(current, ProjectService.Json); status.Text = "Private baseline saved. Stage JSON, review the entire transaction, then commit once.";
    }
    private async Task StageAsync()
    {
        if (current is null) throw new InvalidOperationException("Begin or select a transaction.");
        var request = JsonSerializer.Deserialize<WorkspaceTransactionStageRequest>(editor.Text, ProjectService.Json) ?? throw new InvalidOperationException("Enter a stage request.");
        current = await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{current.Id}/stage", request); ResetEditor(); ShowState(); review.Text = JsonSerializer.Serialize(current, ProjectService.Json); status.Text = "Stage saved privately. The workspace head is unchanged.";
    }
    private async Task PreviewAsync()
    {
        if (current is null || dirty) throw new InvalidOperationException("Stage or reset the editor draft before reviewing saved stages.");
        preview = await client.PostAsync<WorkspaceTransactionPreview>($"/api/transactions/{current.Id}/preview", new WorkspaceTransactionVersionRequest(current.Version));
        review.Text = JsonSerializer.Serialize(preview, ProjectService.Json); commit.Enabled = preview.CanCommit; status.Text = preview.CanCommit ? "Review every staged request and result hash before committing." : "Cannot commit: workspace drift or no net changes. Inspect the review; abort and begin again if needed.";
    }
    private async Task CommitAsync()
    {
        if (current is null || preview is null || !preview.CanCommit || dirty) throw new InvalidOperationException("Review the saved transaction before committing.");
        current = await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{current.Id}/commit", new WorkspaceTransactionCommitRequest(current.Version, preview.ReviewSha256));
        preview = null; commit.Enabled = false; ShowState(); review.Text = JsonSerializer.Serialize(current, ProjectService.Json); status.Text = "Transaction committed as one workspace revision. The durable receipt supports safe commit retries.";
    }
    private async Task AbortAsync()
    {
        if (current is null || dirty) throw new InvalidOperationException("Stage or reset the editor draft first.");
        current = await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{current.Id}/abort", new WorkspaceTransactionVersionRequest(current.Version)); ShowState(); review.Text = JsonSerializer.Serialize(current, ProjectService.Json); status.Text = "Private transaction aborted. The workspace head was not changed.";
    }
    private async Task SchedulingAsync()
    {
        var value = await client.GetAsync<WorkspaceConcurrencyStatus>("/api/concurrency"); status.Text = $"Writers: {value.ActiveMutations}/{value.MaximumConcurrentMutations} active • {value.QueuedMutations}/{value.MaximumQueuedMutations} queued • exclusive compound operation: {value.ExclusiveActive}";
    }
    internal async Task<string[]> ExerciseOfflineWorkflowForTestAsync(string root)
    {
        fixture = true; await RefreshAsync(); await BeginAsync(); var id = current!.WorkspaceId; var baseHash = current.BaseSha256;
        editor.Text = JsonSerializer.Serialize(new WorkspaceTransactionStageRequest(0, XmlEdits: [new("set_text", "//Program[@Name='Demo']/Description", "Private program description")]), ProjectService.Json); await StageAsync();
        editor.Text = JsonSerializer.Serialize(new WorkspaceTransactionStageRequest(current!.Version, DocumentationEdits: [new("//Controller/Tags/Tag[@Name='Record']", "description", "es-ES", "Transacción GUI")]), ProjectService.Json); await StageAsync();
        if ((await client.GetAsync<ProjectInfo>("/api/projects/" + id)).CurrentSha256 != baseHash) throw new InvalidOperationException("GUI stage leaked into the current workspace.");
        await PreviewAsync(); if (!commit.Enabled || preview!.Stages.Length != 2) throw new InvalidOperationException("GUI did not review both saved stages.");
        var request = new WorkspaceTransactionCommitRequest(current!.Version, preview.ReviewSha256); await CommitAsync(); var after = await client.GetAsync<ProjectInfo>("/api/projects/" + id);
        if (after.Revision != 1 || current!.Status != "committed") throw new InvalidOperationException("GUI transaction did not commit once.");
        await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{current.Id}/commit", request);
        if ((await client.GetAsync<ProjectInfo>("/api/projects/" + id)).Revision != 1) throw new InvalidOperationException("A GUI commit retry applied twice.");
        await RefreshAsync(); await BeginAsync(); await AbortAsync(); if (current!.Status != "aborted") throw new InvalidOperationException("GUI did not abort an open transaction.");
        status.Text = "Verified private stages, full review, single-revision commit, safe retry and abort. No native/controller operation.";
        return ["Begin pinned private transaction", "Stage XML and language documentation together", "Preserve current workspace until commit", "Review complete stage/result hashes", "Commit once and retry safely", "Abort an open transaction"];
    }
}
