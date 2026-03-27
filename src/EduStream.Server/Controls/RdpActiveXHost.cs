using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EduStream.Server.Controls;

/// <summary>
/// 설치된 Remote Desktop ActiveX를 우선순위에 따라 선택해 호스팅합니다.
/// </summary>
public sealed class RdpActiveXHost : AxHost
{
    private RdpActiveXHost(string clsid)
        : base(clsid)
    {
    }

    public dynamic ActiveXControl => GetOcx();

    public static RdpActiveXHost CreateBestAvailable()
    {
        foreach (var clsid in CandidateClsids)
        {
            try
            {
                var host = new RdpActiveXHost(clsid);
                host.CreateControl();
                return host;
            }
            catch
            {
                // 다음 후보를 시도합니다.
            }
        }

        throw new InvalidOperationException("No supported Remote Desktop ActiveX control is installed.");
    }

    private static IReadOnlyList<string> CandidateClsids { get; } =
    [
        "54d38bf7-b1ef-4479-9674-1bd6ea465258", // MsRdpClient10
        "a3bc03a0-041d-42e3-ad22-882b7865c9c5", // MsRdpClient9
        "8b918b82-7985-4c24-89df-c33ad2bbfbcd", // MsRdpClient8NotSafeForScripting
        "791fa017-2de3-492e-acc5-53c67a2b94d0", // MsRdpClient7
        "6ae29350-321b-42be-bbe5-12fb5270a0be"  // MsTscAx
    ];
}
