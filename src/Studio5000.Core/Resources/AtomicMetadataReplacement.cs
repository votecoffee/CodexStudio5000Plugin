namespace Studio5000.Core.Resources;

// Retries only the same already-written metadata bytes. Callers retain their
// dispatch intent; no native/process/SCM action is supplied or redispatched.
public static class AtomicMetadataReplacement
{
    public static void Replace(string temporary, string target, Action<string, string>? replace = null, Action<int>? delay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (replace is null) File.Replace(temporary, target, null); else replace(temporary, target);
                return;
            }
            // 1175 preserves both names. 1176/1177 may change them, so their
            // outcome stays uncertain. Missing names likewise prohibit retry.
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 1175 &&
                attempt < 6 && File.Exists(temporary) && File.Exists(target))
            {
                if (delay is null) Thread.Sleep(25 << attempt); else delay(25 << attempt);
            }
        }
    }
}
