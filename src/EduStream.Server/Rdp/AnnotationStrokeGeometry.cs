using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace EduStream.Server.Rdp;

/// <summary>도형 외곽선에 닿은 스트로크를 지운다. 흰색 선을 덧칠하지 않는다.</summary>
internal static class AnnotationStrokeGeometry
{
    public static bool HitByEraser(AnnotationStroke stroke, AnnotationStroke eraser)
    {
        var outline = Outline(stroke);
        var path = eraser.Points;
        double radius = (Math.Max(1, stroke.StrokeWidth) + Math.Max(1, eraser.StrokeWidth)) / 2.0;
        for (int i = 1; i < outline.Length; i++)
            for (int j = 1; j < path.Count; j++)
                if (Intersects(outline[i-1], outline[i], path[j-1], path[j]) ||
                    Distance(outline[i-1], path[j-1], path[j]) <= radius ||
                    Distance(outline[i], path[j-1], path[j]) <= radius ||
                    Distance(path[j-1], outline[i-1], outline[i]) <= radius ||
                    Distance(path[j], outline[i-1], outline[i]) <= radius) return true;
        return false;
    }

    private static PointF[] Outline(AnnotationStroke stroke)
    {
        if (stroke.Points.Count < 2) return Array.Empty<PointF>();
        var a = stroke.Points[0];
        var b = stroke.Points[^1];
        if (stroke.Tool == AnnotationTool.Rectangle)
            return new PointF[] { a, new(b.X, a.Y), b, new(a.X, b.Y), a };
        if (stroke.Tool == AnnotationTool.Circle)
            return Enumerable.Range(0, 65).Select(i => new PointF(
                (a.X+b.X)/2f + Math.Abs(b.X-a.X)/2f * (float)Math.Cos(i*Math.PI/32),
                (a.Y+b.Y)/2f + Math.Abs(b.Y-a.Y)/2f * (float)Math.Sin(i*Math.PI/32))).ToArray();
        return stroke.Points.Select(p => (PointF)p).ToArray();
    }

    private static double Distance(PointF p, PointF a, PointF b)
    {
        double dx=b.X-a.X, dy=b.Y-a.Y;
        double length=dx*dx+dy*dy;
        double t=length==0 ? 0 : Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
        return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));
    }

    private static bool Intersects(PointF a, PointF b, PointF c, PointF d)
    {
        static double Cross(PointF x, PointF y, PointF z) =>
            (y.X-x.X)*(double)(z.Y-x.Y)-(y.Y-x.Y)*(double)(z.X-x.X);
        return Math.Max(a.X,b.X)>=Math.Min(c.X,d.X) && Math.Max(c.X,d.X)>=Math.Min(a.X,b.X) &&
               Math.Max(a.Y,b.Y)>=Math.Min(c.Y,d.Y) && Math.Max(c.Y,d.Y)>=Math.Min(a.Y,b.Y) &&
               Cross(a,b,c)*Cross(a,b,d)<=0 && Cross(c,d,a)*Cross(c,d,b)<=0;
    }
}
