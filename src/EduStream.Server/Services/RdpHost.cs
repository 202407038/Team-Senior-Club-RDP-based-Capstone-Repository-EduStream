using System.Windows.Forms;
using System.Windows.Forms.Integration;
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
                return (int)ocx.Connected != 0;
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

        dynamic ocx = _rdpControl.ActiveXControl;
        ocx.Server = serverAddress.Trim();
        ocx.UserName = userName?.Trim() ?? string.Empty;
        ocx.ColorDepth = 32;

        if (_hostSurface is not null)
        {
            // WindowsFormsHost already acts as the parent window, so avoid forcing
            // UIParentWindowHandle because availability differs across RDP OCX versions.
            ocx.DesktopWidth = Math.Max((int)_hostSurface.ActualWidth, 640);
            ocx.DesktopHeight = Math.Max((int)_hostSurface.ActualHeight, 360);
        }

        try
        {
            dynamic advancedSettings = ocx.AdvancedSettings9;
            advancedSettings.SmartSizing = true;
            advancedSettings.EnableCredSspSupport = true;
            advancedSettings.ClearTextPassword = password ?? string.Empty;
        }
        catch
        {
            dynamic advancedSettings = ocx.AdvancedSettings2;
            advancedSettings.ClearTextPassword = password ?? string.Empty;
        }

        _logSink.Write($"Starting RDP connection to {serverAddress} as {userName}.");
        ocx.Connect();
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
            dynamic ocx = _rdpControl.ActiveXControl;
            if ((int)ocx.Connected != 0)
            {
                ocx.Disconnect();
                _logSink.Write("RDP connection closed.");
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"RDP disconnect failed: {ex.Message}");
        }
    }
}
