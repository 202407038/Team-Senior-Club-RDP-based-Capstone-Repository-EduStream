using System.Collections.Generic;
using System.Drawing;

namespace EduStream.ShareHost
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
        /// <summary>교수자 화면용 이름. 장치 이름 대신 "모니터 1 (주 모니터) · 2560×1440"처럼 보인다.</summary>
        public string Label
        {
            get
            {
                var end = DeviceName.Length;
                while (end > 0 && char.IsDigit(DeviceName[end - 1])) end--;
                var digits = DeviceName[end..];
                var name = digits.Length > 0 ? "모니터 " + digits : (DisplayName.Length > 0 ? DisplayName : "모니터");
                return $"{name}{(IsPrimary ? " (주 모니터)" : string.Empty)} · {Width}×{Height}";
            }
        }
        public override string ToString() => Label;
    }
}
