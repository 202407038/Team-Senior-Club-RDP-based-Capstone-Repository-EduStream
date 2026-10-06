using System.Drawing;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class ProfessorViewerReleaseTests
{
    [Fact]
    public void Release_FromAnotherThread_RemovesParentAndDisposes()
    {
        Forms.Form? form = null;
        Forms.Panel? panel = null;
        using var ready = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            form = new Forms.Form { ShowInTaskbar = false, Width = 320, Height = 240 };
            panel = new Forms.Panel { Size = new Size(120, 80) };
            form.Controls.Add(panel);
            form.Show();
            panel.CreateControl();
            ready.Set();
            Forms.Application.Run();
        })
        { IsBackground = true, Name = "EduStream-ViewerRelease-STA" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var connection = new ProfessorViewerConnection(panel!);
            connection.ReleaseAfterSharingStopped();
            Assert.True(panel!.IsDisposed);
        }
        finally
        {
            form!.BeginInvoke(new Action(Forms.Application.ExitThread));
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void Release_WhenDisposeFails_StaysIncompleteUntilRetrySucceeds()
    {
        var panel = new FailOncePanel();
        var connection = new ProfessorViewerConnection(panel);

        var ex = Assert.Throws<InvalidOperationException>(() => connection.ReleaseAfterSharingStopped());
        Assert.Contains("해제 실패", ex.Message);
        Assert.False(panel.IsDisposed);

        connection.ReleaseAfterSharingStopped();
        Assert.True(panel.IsDisposed);
    }

    private sealed class FailOncePanel : Forms.Panel
    {
        private bool _fail = true;

        protected override void Dispose(bool disposing)
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("해제 실패");
            }

            base.Dispose(disposing);
        }
    }
}
