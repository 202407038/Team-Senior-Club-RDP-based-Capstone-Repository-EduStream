using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EduStream.Core.Common;
using TextBox = System.Windows.Controls.TextBox;

namespace EduStream.Client.Behaviors;

/// <summary>키 입력·붙여넣기·드롭에 동일 규칙 적용. 한글 IME 조합 중에는 문자열을 수정하지 않는다.</summary>
public static class StudentIdentityInput
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(StudentIdentityInput), new PropertyMetadata("", Changed));
    public static string GetKind(DependencyObject obj) => (string)obj.GetValue(KindProperty);
    public static void SetKind(DependencyObject obj, string value) => obj.SetValue(KindProperty, value);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.OldValue is not "" || e.NewValue is "") return;
        bool composing = false, updating = false;
        void Normalize()
        {
            if (composing || updating) return;
            var result = GetKind(box) == "Name" ? StudentInputRules.Name(box.Text) : StudentInputRules.StudentId(box.Text);
            if (box.Text == result) return;
            var caret = box.CaretIndex;
            updating = true;
            try { box.SetCurrentValue(TextBox.TextProperty, result); box.CaretIndex = Math.Min(caret, result.Length); }
            finally { updating = false; }
        }
        TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(box, (_, _) => composing = true);
        box.PreviewTextInput += (_, _) => { composing = false; box.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Normalize)); };
        box.TextChanged += (_, _) => Normalize();
        box.LostKeyboardFocus += (_, _) => { composing = false; Normalize(); };
        box.Loaded += (_, _) => Normalize();
    }
}
