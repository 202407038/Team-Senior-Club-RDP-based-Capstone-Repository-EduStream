using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Reflection;
using EduStream.Core.Logging;
using EduStream.Server.Controls;

namespace EduStream.Server.Services;

/// <summary>
/// Hosts the built-in Microsoft Remote Desktop ActiveX control inside WPF.
/// This is intended as a quick integration spike, not the final streaming architecture.
/// </summary>
public sealed class RdpHost
{
    private readonly ILogSink _logSink;
    private WindowsFormsHost? _hostSurface;
    private RdpActiveXHost? _rdpControl;

    public RdpHost(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public bool IsAttached => _hostSurface is not null;

    public bool IsConnected
    {
        get
        {
            if (_rdpControl is null)
            {
                return false;
            }

            try
            {
                dynamic ocx = _rdpControl.ActiveXControl;
                return TryGetIntProperty(ocx, "Connected") != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void AttachHost(WindowsFormsHost hostSurface)
    {
        ArgumentNullException.ThrowIfNull(hostSurface);

        _hostSurface = hostSurface;
        EnsureControl();
        _logSink.Write("RDP preview surface attached.");
    }

    public Task StartHostAsync(string serverAddress, string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            throw new ArgumentException("RDP server address is required.", nameof(serverAddress));
        }

        EnsureControl();

        if (_rdpControl is null)
        {
            throw new InvalidOperationException("RDP ActiveX control is not available on this machine.");
        }

        if (IsConnected)
        {
            DisconnectInternal();
        }

        var ocx = _rdpControl.ActiveXControl;
        TrySetProperty(ocx, "Server", serverAddress.Trim());
        TrySetProperty(ocx, "UserName", userName?.Trim() ?? string.Empty);
        TrySetProperty(ocx, "ColorDepth", 32);

        if (_hostSurface is not null)
        {
            TrySetProperty(ocx, "DesktopWidth", Math.Max((int)_hostSurface.ActualWidth, 640));
            TrySetProperty(ocx, "DesktopHeight", Math.Max((int)_hostSurface.ActualHeight, 360));
        }

        var advancedSettings =
            TryGetProperty(ocx, "AdvancedSettings9") ??
            TryGetProperty(ocx, "AdvancedSettings8") ??
            TryGetProperty(ocx, "AdvancedSettings7") ??
            TryGetProperty(ocx, "AdvancedSettings6") ??
            TryGetProperty(ocx, "AdvancedSettings2");

        if (advancedSettings is not null)
        {
            TrySetProperty(advancedSettings, "SmartSizing", true);
            TrySetProperty(advancedSettings, "EnableCredSspSupport", true);
            TrySetProperty(advancedSettings, "ClearTextPassword", password ?? string.Empty);
        }

        _logSink.Write($"Starting RDP connection to {serverAddress} as {userName}.");
        InvokeMember(ocx, "Connect");
        return Task.CompletedTask;
    }

    public Task StopHostAsync()
    {
        DisconnectInternal();
        return Task.CompletedTask;
    }

    private void EnsureControl()
    {
        if (_hostSurface is null || _rdpControl is not null)
        {
            return;
        }

        _rdpControl = RdpActiveXHost.CreateBestAvailable();
        _rdpControl.Dock = DockStyle.Fill;
        _hostSurface.Child = _rdpControl;
    }

    private void DisconnectInternal()
    {
        if (_rdpControl is null)
        {
            return;
        }

        try
        {
            var ocx = _rdpControl.ActiveXControl;
            if (TryGetIntProperty(ocx, "Connected") != 0)
            {
                InvokeMember(ocx, "Disconnect");
                _logSink.Write("RDP connection closed.");
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"RDP disconnect failed: {ex.Message}");
        }
    }

    private static void TrySetProperty(object target, string name, object? value)
    {
        try
        {
            InvokeMember(target, name, BindingFlags.SetProperty, value);
        }
        catch
        {
            // RDP ActiveX control versions expose slightly different COM surfaces.
        }
    }

    private static object? TryGetProperty(object target, string name)
    {
        try
        {
            return InvokeMember(target, name, BindingFlags.GetProperty);
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetIntProperty(object target, string name)
    {
        try
        {
            var value = TryGetProperty(target, name);
            if (value is null)
            {
                return 0;
            }

            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static object? InvokeMember(object target, string name)
    {
        return InvokeMember(target, name, BindingFlags.InvokeMethod);
    }

    private static object? InvokeMember(object target, string name, BindingFlags flags, object? value = null)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | flags,
            binder: null,
            target,
            value is null ? null : [value]);
    }
}
