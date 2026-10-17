using System.ComponentModel;
using System.Diagnostics;

namespace OpenVideoSmartShare.Infrastructure;

internal enum FirewallState
{
    /// <summary>The state could not be read.</summary>
    Unknown,

    /// <summary>Windows Firewall is off for the active network profiles.</summary>
    Disabled,

    /// <summary>An enabled inbound allow rule exists for the executable.</summary>
    Allowed,

    /// <summary>No inbound rule for the executable: TVs cannot connect.</summary>
    Missing,

    /// <summary>An inbound block rule exists (e.g. the Windows prompt was cancelled once).</summary>
    Blocked,
}

/// <summary>
/// Checks the Windows Firewall rules for this executable (read-only, no elevation needed) and
/// adds an inbound allow rule through an elevated netsh, which triggers the UAC prompt.
/// </summary>
internal static class FirewallHelper
{
    private const string RuleName = "OpenVideoSmartShare";
    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ErrorCancelled = 1223;

    public static FirewallState Check(string executablePath)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null)
            {
                return FirewallState.Unknown;
            }

            dynamic policy = Activator.CreateInstance(policyType)!;
            int profiles = policy.CurrentProfileTypes;
            var enabled = false;
            foreach (var profile in new[] { 1, 2, 4 })
            {
                if ((profiles & profile) != 0 && (bool)policy.FirewallEnabled[profile])
                {
                    enabled = true;
                }
            }

            if (!enabled)
            {
                return FirewallState.Disabled;
            }

            var target = Normalize(executablePath);
            var allowed = false;
            var blocked = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? application = rule.ApplicationName;
                if (application == null || !(bool)rule.Enabled || (int)rule.Direction != DirectionIn
                    || ((int)rule.Profiles & profiles) == 0 || Normalize(application) != target)
                {
                    continue;
                }

                switch ((int)rule.Action)
                {
                    case ActionBlock:
                        blocked = true;
                        break;
                    case ActionAllow:
                        allowed = true;
                        break;
                }
            }

            // Block rules win over allow rules in Windows Firewall.
            return blocked ? FirewallState.Blocked : allowed ? FirewallState.Allowed : FirewallState.Missing;
        }
        catch (Exception ex)
        {
            Log.Warn("Cannot read the firewall configuration", ex);
            return FirewallState.Unknown;
        }
    }

    /// <summary>
    /// Replaces the inbound rules of the executable with an allow rule. Shows the UAC prompt;
    /// returns false when the user declines it or netsh fails.
    /// </summary>
    public static bool AllowInbound(string executablePath)
    {
        var program = executablePath.Replace("\"", "");
        var command =
            $"/c netsh advfirewall firewall delete rule name=all dir=in program=\"{program}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{program}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null || !process.WaitForExit(60_000))
            {
                return false;
            }

            Log.Info($"Firewall rule update exit code {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Firewall rule: elevation declined");
            return false;
        }
        catch (Win32Exception ex)
        {
            Log.Warn("Firewall rule update failed", ex);
            return false;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"'))).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
