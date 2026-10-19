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
        Assert.Equal(96, operations.Length); Assert.Equal(96, operations.Select(o => o.Name).Distinct().Count());
        foreach (var operation in operations) ExtensionSchema.CheckDefinition(operation.InputSchema);
        Assert.Equal("control", operations.Single(o => o.Name == "studio_sdk_start_job").Effect);
        Assert.Equal("control", operations.Single(o => o.Name == "studio_watch_start").Effect);
        Assert.Equal("preview", operations.Single(o => o.Name == "viewdesigner_preview_changes").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "viewdesigner_screen_summary").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "ftview_tag_references").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "ftview_resolve_references").Effect);
        Assert.Equal("read", operations.Single(o => o.Name == "studio_compare_projects").Effect);
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
    }
}
