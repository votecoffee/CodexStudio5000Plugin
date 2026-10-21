using Studio5000.Extensions.Rockwell;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExtensionCatalogTests
{
    [Fact]
    public void AllExistingFeaturesHaveValidatedCatalogSchemasAndExplicitEffects()
    {
        IWorkspaceExtension[] modules = [new LogixExtension(), new FactoryTalkExtension(), new ViewDesignerExtension(), new WatchesExtension()];
        var operations = modules.SelectMany(x => x.Describe()).ToArray();
        Assert.Equal(153, operations.Length); Assert.Equal(153, operations.Select(o => o.Name).Distinct().Count());
        Assert.Equal("read", operations.Single(o => o.Name == "studio_module_connections").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_module_connections_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_module_connections_apply").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_controller_tag_rename_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_controller_tag_rename_apply").Effect);
        foreach (var prefix in new[] { "ftview", "viewdesigner" })
        {
            foreach (var verb in new[] { "list", "get", "browse", "preview", "verify" }) Assert.Equal("read", operations.Single(o => o.Name == prefix + "_languages_" + verb).Effect);
            foreach (var verb in new[] { "register", "apply", "export" }) Assert.Equal("apply", operations.Single(o => o.Name == prefix + "_languages_" + verb).Effect);
        }
        foreach (var operation in operations) ExtensionSchema.CheckDefinition(operation.InputSchema);
        Assert.Equal("control", operations.Single(o => o.Name == "studio_sdk_start_job").Effect);
        Assert.Equal("control", operations.Single(o => o.Name == "studio_watch_start").Effect);
        Assert.Equal("preview", operations.Single(o => o.Name == "viewdesigner_preview_changes").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "viewdesigner_screen_summary").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "ftview_tag_references").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "ftview_resolve_references").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_compare_projects").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_documentation_browse").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_documentation_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_documentation_apply").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_documentation_export").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_merge_mapping_bind").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_merge_mapping_save").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_session_save").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_comparison_session_recovery_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_session_recovery_apply").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_merge_mapping_export").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_merge_mapping_import").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_review_export").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_comparison_reviews").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_comparison_review_get").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_review_forget").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_comparison_annotations").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_annotations_update").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_comparison_annotations_export").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_compare_locate").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_value_merge_browse").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_ladder_preview").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_tag_references").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_cip_watch_plan").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_watch_preflight").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_watch_template_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_watch_template_export").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_watch_template_import").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_watch_rebind_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_watch_rebind_apply").Effect);
        foreach (var name in new[] { "studio_logic_trace", "studio_rung_evaluate", "studio_trend_analyze", "studio_logic_value_merge_preview", "studio_version_compatibility_preview" }) Assert.Equal("read", operations.Single(o => o.Name == name).Effect);
        foreach (var name in new[] { "studio_diagnostic_capture", "studio_diagnostic_trigger_arm", "studio_trend_view_save", "studio_trend_export", "studio_logic_value_merge_apply" }) Assert.Equal("apply", operations.Single(o => o.Name == name).Effect);
        foreach (var name in new[] { "studio_version_compatibility_start", "studio_version_compatibility_readback" }) Assert.Equal("control", operations.Single(o => o.Name == name).Effect);
        foreach (var name in new[] { "studio_validation_profiles", "studio_validation_preview", "studio_validation_receipt" }) Assert.Equal("read", operations.Single(o => o.Name == name).Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_validation_record").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_legacy_review_preview").Effect);
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_legacy_review_prepare").Effect);
        Assert.Contains("keep current values", operations.Single(o => o.Name == "studio_logic_value_merge_preview").Description);
    }
}
