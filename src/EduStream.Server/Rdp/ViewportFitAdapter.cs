using System;
using System.Drawing;

namespace EduStream.Server.Rdp;

/// <summary>
/// 뷰포트 맞춤 어댑터 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public sealed class ViewportFitAdapter : IViewportFitAdapter
{
    /// <summary>
    /// 화면 맞춤 모드로 뷰포트 크기 계산
    /// </summary>
    public ViewportInfo CalculateFitViewport(Size sourceSize, Size targetSize, FitMode fitMode)
    {
        double zoomLevel = 1.0;
        Size viewportSize = targetSize;
        Rectangle sourceRect = new Rectangle(0, 0, sourceSize.Width, sourceSize.Height);

        switch (fitMode)
        {
            case FitMode.Fit:
                // 원본 비율 유지하며 전체 화면에 맞춤
                double scaleX = (double)targetSize.Width / sourceSize.Width;
                double scaleY = (double)targetSize.Height / sourceSize.Height;
                zoomLevel = Math.Min(scaleX, scaleY);
                viewportSize = new Size(
                    (int)(sourceSize.Width * zoomLevel),
                    (int)(sourceSize.Height * zoomLevel)
                );
                break;

            case FitMode.FitHeight:
                // 원본 비율 유지하며 높이에 맞춤
                zoomLevel = (double)targetSize.Height / sourceSize.Height;
                viewportSize = new Size(
                    (int)(sourceSize.Width * zoomLevel),
                    targetSize.Height
                );
                break;

            case FitMode.FitWidth:
                // 원본 비율 유지하며 너비에 맞춤
                zoomLevel = (double)targetSize.Width / sourceSize.Width;
                viewportSize = new Size(
                    targetSize.Width,
                    (int)(sourceSize.Height * zoomLevel)
                );
                break;

            case FitMode.Original:
                // 원본 크기 그대로
                zoomLevel = 1.0;
                viewportSize = sourceSize;
                break;

            case FitMode.Stretch:
                // 뷰포트에 꽉 차게 늘림 (비율 변환)
                zoomLevel = Math.Max(
                    (double)targetSize.Width / sourceSize.Width,
                    (double)targetSize.Height / sourceSize.Height
                );
                viewportSize = targetSize;
                break;
        }

        return new ViewportInfo
        {
            ViewportSize = viewportSize,
            ZoomLevel = zoomLevel,
            SourceRect = sourceRect
        };
    }

    /// <summary>
    /// 줌 배율 적용 후 뷰포트 크기 계산
    /// </summary>
    public Size CalculateZoomedViewport(Size sourceSize, double zoomLevel)
    {
        if (zoomLevel <= 0)
            throw new ArgumentException("줌 배율은 0보다 커야 합니다.", nameof(zoomLevel));

        return new Size(
            (int)(sourceSize.Width * zoomLevel),
            (int)(sourceSize.Height * zoomLevel)
        );
    }

    /// <summary>
    /// 뷰포트 내 좌표가 유효한지 확인
    /// </summary>
    public bool IsValidViewportPoint(Point point, Size viewportSize)
    {
        return point.X >= 0 && point.X < viewportSize.Width &&
               point.Y >= 0 && point.Y < viewportSize.Height;
    }
}
