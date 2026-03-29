using System.Windows.Forms;

namespace EduStream.Server.Controls;

/// <summary>
/// Thin ActiveX host for the built-in Microsoft Remote Desktop client control.
/// It tries the Windows 10+ control first and falls back to the Windows 8.1-era control.
/// </summary>
internal sealed class RdpActiveXHost : AxHost
{
    private const string Client10Clsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private const string Client9Clsid = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    private RdpActiveXHost(string clsid)
        : base(clsid)
    {
    }

    public dynamic ActiveXControl => GetOcx();

    public static RdpActiveXHost CreateBestAvailable()
    {
        try
        {
            return new RdpActiveXHost(Client10Clsid);
        }
        catch
        {
            return new RdpActiveXHost(Client9Clsid);
        }
    }
}
