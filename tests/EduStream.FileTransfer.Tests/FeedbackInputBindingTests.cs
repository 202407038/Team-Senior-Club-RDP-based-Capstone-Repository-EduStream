using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using EduStream.Client.Behaviors;

namespace EduStream.FileTransfer.Tests;

// Window/Show/클립보드/키보드 주입 없이 실제 TextBox와 바인딩만 검사한다.
public sealed class FeedbackInputBindingTests
{
    [Theory]
    [InlineData("Name", "가나다라마바사123", "가나다라마바사")]
    [InlineData("Name", "가  나다😀", "가 나다")]
    [InlineData("StudentId", "000-123한글", "000123")]
    public Task InputChangeFiltersTextWithoutDetachingBinding(string kind, string input, string expected)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var model = new ValueHolder();
                var box = new TextBox(); StudentIdentityInput.SetKind(box, kind);
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(ValueHolder.Value)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                box.SetCurrentValue(TextBox.TextProperty, input);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(expected, box.Text);
                Assert.NotNull(BindingOperations.GetBindingExpression(box, TextBox.TextProperty));
                box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal(expected, model.Value);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    public sealed class ValueHolder { public string Value { get; set; } = ""; }
}
