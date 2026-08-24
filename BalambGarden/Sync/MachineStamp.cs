using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BalambGarden.Sync;

/// <summary>This install's machine fingerprint - the one signal that tells a CLONE from a
/// healthy install (identity amendment, ruled 2026-08-23). A copied plugin folder counts
/// forward from the same era with the same numbers on both machines and looks perfect from
/// the inside; only the machine disagreeing gives it away, and <c>EraGuard</c> retires the
/// era on that alone.
///
/// <para>HASHED AT THE SOURCE. The raw MachineGuid is never stored, never logged, and never
/// held past the read that produced it - the value that lives in config is a SHA-256 of it
/// and nothing else. The hash stays LOCAL by contract: it is config's memory, it never rides
/// a wire receipt, and the Engine treats it as an opaque string it only ever compares for
/// equality. A fingerprint that travelled would undo the pseudonymity rulings by itself.</para>
///
/// <para>Registry-first because MachineGuid survives a rename and a reinstall of the plugin;
/// the machine name is the fallback, and a fallback that changes only ever costs a spurious
/// era mint, which is the direction the whole instrument fails in.</para></summary>
internal static class MachineStamp
{
    /// <summary>Computed once per session. The answer cannot change while the game is
    /// running, and every receipt save asks for it.</summary>
    private static string? cached;

    internal static string Hash() => cached ??= Compute();

    private static string Compute()
    {
        var seed = ReadMachineGuid() ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex)
        {
            // Never fatal: a fingerprint we cannot read is a fingerprint that falls back, not
            // a startup that fails.
            Plugin.Log.Warning(
                $"[Sync] machine guid unreadable ({ex.Message}) - falling back to the machine name");
            return null;
        }
    }
}
