using System.Text.Json;

namespace Studio5000.Desktop;

internal static class ControllerTargetChoices
{
    public static bool Bind(ComboBox control, JsonElement capabilities, bool empty = true)
    {
        control.DataSource = null; control.DisplayMember = ""; control.Items.Clear();
        if (empty) control.Items.Add("");
        foreach (var target in capabilities.GetProperty("controllerTargets").EnumerateArray()) control.Items.Add(target.GetProperty("id").GetString()!);
        var any = capabilities.TryGetProperty("allowAnyControllerPath", out var allowed) && allowed.GetBoolean();
        control.DropDownStyle = any ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
        control.AccessibleDescription = any ? "Select a target ID or enter path: followed by an explicit SDK communications path." : "Select a configured controller target ID.";
        control.MaxLength = 2053;
        if (control.Items.Count != 0) control.SelectedIndex = 0;
        return any;
    }
}
