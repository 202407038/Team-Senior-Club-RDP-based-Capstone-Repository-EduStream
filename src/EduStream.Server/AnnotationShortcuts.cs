using System.Windows.Input;

namespace EduStream.Server;

public static class AnnotationShortcuts
{
    public static bool IsUndo(bool drawing, bool busy, bool editingText, bool drawingGesture,
        Key key, ModifierKeys modifiers) =>
        drawing && !busy && !editingText && !drawingGesture && key == Key.Z && modifiers == ModifierKeys.Control;
}
