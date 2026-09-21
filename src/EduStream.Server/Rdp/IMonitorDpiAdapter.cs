using System.Collections.Generic;
using System.Drawing;

namespace EduStream.Server.Rdp
{
    /// <summary>
    /// 모니터 DPI 스케일링 보정 어댑터 인터페이스
    /// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
    /// </summary>
    public interface IMonitorDpiAdapter
    {
        /// <summary>
        /// 시스템의 모든 모니터 정보 조회
        /// </summary>
        IReadOnlyList<MonitorInfo> GetMonitors();
        
        /// <summary>
        /// 논리 좌표를 실제 픽셀 좌표로 변환 (DPI 보정)
        /// </summary>
        Point LogicalToPhysical(Point logicalPoint, MonitorInfo monitor);
        
        /// <summary>
        /// 실제 픽셀 좌표를 논리 좌표로 변환 (DPI 보정)
        /// </summary>
        Point PhysicalToLogical(Point physicalPoint, MonitorInfo monitor);
        
        /// <summary>
        /// 주 모니터 정보 조회
        /// </summary>
        MonitorInfo GetPrimaryMonitor();
    }

    /// <summary>
    /// 모니터 정보
    /// </summary>
    public sealed class MonitorInfo
    {
        public string DeviceName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public int Left { get; init; }
        public int Top { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int DpiX { get; init; }
        public int DpiY { get; init; }
        public double ScaleFactor { get; init; } // 1.0 = 100%, 1.25 = 125%, 1.5 = 150%
        public bool IsPrimary { get; init; }
    }
}
