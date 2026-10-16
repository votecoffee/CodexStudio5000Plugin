namespace Studio5000.Core;

public static class SdkDiagnostics
{
    public const string AcceptedEditsExist = "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM";

    // Preserve the SDK outcome and attempted calls; this is guidance, never a retry or rollback.
    public static SdkBridgeResult Describe(SdkBridgeResult result) =>
        result.Status == "failed" && result.Error?.Contains(AcceptedEditsExist, StringComparison.Ordinal) == true
            ? result with { Events = [..result.Events, new("native_action_required",
                "The program already contains accepted logic edits, so another partial import is blocked. SDK 2.1.974 has no standalone finalize/assemble call. Complete the authorized existing edits in Studio; do not retry the import or automatically cancel edits. For a future API-only change, choose FinalizeEdits on the initial import.")] }
            : result;
}
